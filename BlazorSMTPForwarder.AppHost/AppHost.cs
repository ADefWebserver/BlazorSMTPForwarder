using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Add Azurite storage emulator for development
// This will automatically start Azurite container or use local installation
var storage = builder.AddAzureStorage("storage");

if (builder.Environment.IsDevelopment())
{
    storage.RunAsEmulator();
}

// Add Blob storage resource
var blobs = storage.AddBlobs("emailblobs");

// Add Table storage resource  
var tables = storage.AddTables("SMTPSettings");

var blazorApp = builder.AddProject<Projects.BlazorSMTPForwarder_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    // Allow the Blazor app to access the blob resource for reading messages
    .WithReference(blobs)
    // Ensure Blazor app also gets a tables connection for settings/password
    .WithReference(tables);

// Add the SMTP Server service with storage dependencies and expose SMTP ports
var smtpServer = builder.AddProject<Projects.BlazorSMTPForwarderSrv>("smtpserversvc")
    .WithReference(blobs)
    .WithReference(tables)
    .WithEndpoint("smtp-port1", endpoint =>
    {
        endpoint.Port = 25;
        endpoint.IsExternal = true;
        endpoint.IsProxied = false; // Allow direct external access without proxy
        endpoint.UriScheme = "tcp";
    })
    .WithEndpoint("smtp-port2", endpoint =>
    {
        endpoint.Port = 2525;
        endpoint.IsExternal = true;
        endpoint.IsProxied = false; // Allow direct external access without proxy
        endpoint.UriScheme = "tcp";
    })
    .WithEndpoint("smtp-port3", endpoint =>
    {
        endpoint.Port = 587;
        endpoint.IsExternal = true;
        endpoint.IsProxied = false; // Allow direct external access without proxy
        endpoint.UriScheme = "tcp";
    });

// Optional: Add dependency so Blazor app can reference SMTP server if needed
blazorApp.WithReference(smtpServer);

builder.Build().Run();
