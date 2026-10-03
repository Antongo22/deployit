using DeployIt.Models;
using DeployIt.Services.Interfaces;

namespace DeployIt.Services;

public sealed class WeatherForecastService : IWeatherForecastService
{
    private static readonly string[] Summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild",
        "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    public IReadOnlyList<WeatherForecast> GetForecast()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        return Enumerable.Range(1, 5)
            .Select(index => new WeatherForecast(
                today.AddDays(index),
                Random.Shared.Next(-20, 55),
                Summaries[Random.Shared.Next(Summaries.Length)]))
            .ToArray();
    }
}
