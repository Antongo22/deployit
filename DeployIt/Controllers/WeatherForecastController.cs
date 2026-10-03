using DeployIt.Models;
using DeployIt.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DeployIt.Controllers;

[ApiController]
[Route("weatherforecast")]
public sealed class WeatherForecastController(IWeatherForecastService weatherForecastService) : ControllerBase
{
    [HttpGet(Name = "GetWeatherForecast")]
    public ActionResult<IReadOnlyList<WeatherForecast>> Get()
    {
        return Ok(weatherForecastService.GetForecast());
    }
}
