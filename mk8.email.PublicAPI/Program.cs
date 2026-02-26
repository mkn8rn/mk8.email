using mk8.email.Application;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure;
using mk8.email.Infrastructure.Environment;

var builder = WebApplication.CreateBuilder(args);

var isDev = builder.Environment.IsDevelopment();
var env = EnvironmentLoader.Load(isDev);

// Add services to the container.
builder.Services.AddInfrastructure(env);
builder.Services.AddApplication();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Seed database
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<ISeederService>();
    await seeder.SeedAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
