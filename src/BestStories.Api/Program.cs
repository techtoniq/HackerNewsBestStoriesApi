using BestStories.Api.Api;
using BestStories.Api.Configuration;
using BestStories.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddBestStories(builder.Configuration);
builder.Services.AddBestStoriesObservability(builder.Configuration);
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseApiSecurityHeaders();
app.UseExceptionHandler();
app.UseApiStatusCodePages();

app.MapHealthEndpoints();
app.MapStoryEndpoints();

app.Run();

public partial class Program
{
}
