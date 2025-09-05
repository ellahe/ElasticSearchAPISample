using Nest;

namespace ElasticSearchAPISample
{

    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public CompletionField NameSuggest { get; set; } = new CompletionField();

        public double Price { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}
