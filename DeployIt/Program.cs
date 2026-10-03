using DeployIt.Services;
using DeployIt.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddScoped<IWeatherForecastService, WeatherForecastService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("../openapi/v1.json", "DeployIt API v1");
        options.DocumentTitle = "DeployIt API — Swagger";
    });
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();
