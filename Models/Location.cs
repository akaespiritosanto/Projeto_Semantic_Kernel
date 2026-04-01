namespace semantic_kernel.Models;

public sealed class Location
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Weather { get; set; }
    public double Temperature { get; set; }
    public DateTime LastUpdated { get; set; }
}
