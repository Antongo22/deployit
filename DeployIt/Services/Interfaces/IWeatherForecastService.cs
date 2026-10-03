using DeployIt.Models;

namespace DeployIt.Services.Interfaces;

public interface IWeatherForecastService
{
    IReadOnlyList<WeatherForecast> GetForecast();
}
