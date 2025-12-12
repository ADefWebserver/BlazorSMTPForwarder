using BlazorSMTPForwarderSrv.Services;
using BlazorSMTPForwarder.ServiceDefaults.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmtpServer.Storage;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Add Azure Storage
builder.AddAzureTableClient("SMTPSettings");
builder.AddAzureBlobClient("emailblobs");

// Configure SMTP Settings
builder.Services.Configure<SmtpServerConfiguration>(builder.Configuration.GetSection("SmtpServer"));

// Register SMTP Services
builder.Services.AddSingleton<IMessageStore, DefaultMessageStore>();
builder.Services.AddHostedService<SmtpServerHostedService>();

var host = builder.Build();
host.Run();
