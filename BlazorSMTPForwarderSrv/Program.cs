using BlazorSMTPForwarderSrv.Services;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Add Azure Storage
builder.AddAzureTableServiceClient("SMTPSettings");
builder.AddAzureBlobServiceClient("emailblobs");

// Configure SMTP Settings
builder.Services.Configure<SmtpServerConfiguration>(builder.Configuration.GetSection("SmtpServer"));

// Register SMTP Services
builder.Services.AddSingleton<ZetianMessageHandler>();
builder.Services.AddHostedService<SmtpServerHostedService>();

var host = builder.Build();
host.Run();
