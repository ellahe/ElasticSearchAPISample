using ElasticSearchAPISample;
using Nest;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------
// سرویس‌ها
// -----------------------------

builder.Services.AddControllers();

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// -----------------------------
// کانفیگ اتصال Elasticsearch
// -----------------------------
var settings = new ConnectionSettings(new Uri("http://localhost:9200"))
    .BasicAuthentication("elastic", "changeme")
    .DefaultIndex("products") // ایندکس پیش‌فرض برای همه عملیات
    .DisableDirectStreaming();
var client = new ElasticClient(settings);
builder.Services.AddSingleton<IElasticClient>(client);

var app = builder.Build();

// -----------------------------
// Middleware
// -----------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// ==============================
// Minimal API Endpoints
// ==============================

// -----------------------------
// تست اتصال Elasticsearch
// -----------------------------
app.MapGet("/test", async (IElasticClient es) =>
{
    var ping = await es.PingAsync();
    return ping.IsValid ? Results.Ok("Elasticsearch is UP") : Results.Problem("Elasticsearch is DOWN");
});

// -----------------------------
// اضافه کردن یک محصول تکی
// -----------------------------
app.MapPost("/products", async (IElasticClient es, Product product) =>
{
    product.NameSuggest = new CompletionField
    {
        Input = new[] { product.Name } // فقط Input، هیچ Contextی
    };

    // IndexDocumentAsync → اضافه کردن یک سند (Document) به ایندکس
    var response = await es.IndexDocumentAsync(product);
    return response.IsValid ? Results.Ok(product) : Results.Problem(response.DebugInformation);
});

// -----------------------------
// Bulk Insert: اضافه کردن چند محصول به صورت همزمان
// -----------------------------
app.MapPost("/products/bulk", async (IElasticClient es, List<Product> products) =>
{
    foreach (var product in products)
    {
        product.NameSuggest = new CompletionField
        {
            Input = product.Name
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries) // هر کلمه جدا
                    .Concat(new[] { product.Name }) // کل رشته هم به Input اضافه شود
                    .ToArray()
        };
    }
    // BulkAsync → اضافه کردن چند سند یکجا (سریع‌تر)
    var bulkResponse = await es.BulkAsync(b => b
        .Index("products")
        .IndexMany(products)
    );

    if (bulkResponse.Errors)
    {
        foreach (var itemWithError in bulkResponse.ItemsWithErrors)
        {
            Console.WriteLine($"Failed to index document {itemWithError.Id}: {itemWithError.Error}");
        }
        return Results.Problem("Some items failed to index");
    }

    return Results.Ok(products);
});

// -----------------------------
// جستجو محصول با Partial Match و فیلتر قیمت و Paging
// -----------------------------
app.MapGet("/products/search", async (IElasticClient es, string name, int page = 1, int pageSize = 10, double? maxPrice = null) =>
{
    // ساخت Query
    var suggestResponse = await es.SearchAsync<Product>(s => s
        .Suggest(su => su
            .Completion("name-suggest", c => c
                .Field(f => f.NameSuggest)
                .Prefix(name)
                .Fuzzy(fz => fz.Fuzziness(Fuzziness.Auto))
            )
        )
    );

    // گرفتن Id های پیشنهادی
    var suggestedNames = suggestResponse.Suggest["name-suggest"]
        .SelectMany(s => s.Options)
        .Select(o => o.Text)
        .ToList();

    // سپس جستجوی اصلی با Paging + Filter
    var response = await es.SearchAsync<Product>(s => s
     .From((page - 1) * pageSize)
     .Size(pageSize)
     .Query(q => q
         .Bool(b =>
         {
             // ساخت Should برای هر پیشنهاد
             foreach (var suggested in suggestedNames)
             {
                 b.Should(sh => sh.Match(m => m
                     .Field(f => f.Name)
                     .Query(suggested)
                 ));
             }

             b.MinimumShouldMatch(1);

             if (maxPrice.HasValue)
             {
                 b.Filter(f => f.Range(r => r.Field(p => p.Price).LessThanOrEquals(maxPrice.Value)));
             }

             return b;
         })
     )
     .Sort(ss => ss.Ascending(p => p.Price))
 );

    return Results.Ok(new
    {
        Total = response.Total,
        Page = page,
        PageSize = pageSize,
        Items = response.Documents
    });
});

// -----------------------------
// Autocomplete / Suggestion
// -----------------------------
app.MapGet("/products/suggest", async (IElasticClient es, string prefix) =>
{
    var suggestResponse = await es.SearchAsync<Product>(s => s
       .Suggest(su => su
           .Completion("name-suggest", c => c
               .Field(f => f.NameSuggest)
               .Prefix(prefix)
               .Fuzzy(fz => fz.Fuzziness(Fuzziness.Auto))
           )
       )
   );

    if (!suggestResponse.Suggest.ContainsKey("name-suggest"))
        return Results.Ok(new List<string>());

    var suggestions = suggestResponse.Suggest["name-suggest"]
        .SelectMany(s => s.Options)
        .Select(o => o.Text)
        .Distinct()
        .ToList();

    return Results.Ok(suggestions);
});

app.Run();
