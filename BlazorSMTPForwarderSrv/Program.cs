using BlazorSMTPForwarderSrv.Services;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Add Azure Storage
builder.AddAzureTableServiceClient("SMTPSettings");
builder.AddAzureBlobServiceClient("emailblobs");

// Configure SmtpServer settings from configuration
builder.Services.AddSingleton<SmtpServerConfiguration>();

// Register TableStorageLogger
builder.Services.AddSingleton<TableStorageLogger>();

// Register SMTP Services
builder.Services.AddSingleton<ZetianMessageHandler>();
builder.Services.AddHostedService<SmtpServerHostedService>();

var host = builder.Build();
host.Run();
