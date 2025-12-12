using System.Net.Mail;
using System.Net;
using Azure.Data.Tables;
using System.Text.Json;

namespace BlazorSMTPForwarder.Web.Services;

/// <summary>
/// Simple test client to verify SMTP server functionality
/// </summary>
public class SmtpTestClient
{
    public static async Task TestSmtpServer(TableServiceClient tableServiceClient, string smtpHost)
    {
        try
        {
            Console.WriteLine("Testing SMTP Server...");

            var servername = await GetServerNameAsync(tableServiceClient);
            var allowedRecipient = await GetAllowedRecipientAsync(tableServiceClient);
            var allowedEmail = allowedRecipient.Contains("@") ? allowedRecipient : $"{allowedRecipient}@{servername}";

            // Test 1: Send email to allowed recipient without authentication (should work)
            await SendTestEmail(tableServiceClient, smtpHost, $"sender@{servername}", allowedEmail,
                "Test Email Without Auth", "This is a test email without authentication.", false);

            // Test 2: Send email to disallowed recipient (should fail)
            await SendTestEmail(tableServiceClient, smtpHost, $"sender@{servername}", "notallowed@example.com",
                "Test Email to Disallowed Recipient", "This should fail.", false);

            // Test 3: Send email to allowed recipient with authentication (should work)
            await SendTestEmail(tableServiceClient, smtpHost, $"sender@{servername}", allowedEmail,
                "Test Email With Auth", "This is a test email with authentication.", true);

            // Test 4: Send email to 'abuse' role account (should work)
            if (!string.IsNullOrWhiteSpace(servername))
            {
                await SendTestEmail(tableServiceClient, smtpHost, $"sender@{servername}", $"abuse@{servername}",
                    "Test Email to abuse", "This is a test email to the abuse mailbox.", false);

                // Test 5: Send email to 'postmaster' role account (should work)
                await SendTestEmail(tableServiceClient, smtpHost, $"sender@{servername}", $"postmaster@{servername}",
                    "Test Email to postmaster", "This is a test email to the postmaster mailbox.", false);
            }

            Console.WriteLine("SMTP Server tests completed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Test failed: {ex.Message}");
        }
    }

    private static async Task<string> GetAllowedRecipientAsync(TableServiceClient tableServiceClient)
    {
        const string SettingsTableName = "SMTPSettings";
        const string PartitionKey = "SmtpServer";
        const string RowKey = "Current";
        try
        {
            var table = tableServiceClient.GetTableClient(SettingsTableName);
            var entityResponse = await table.GetEntityAsync<TableEntity>(PartitionKey, RowKey);
            var entity = entityResponse.Value;

            if (entity.TryGetValue("AllowedRecipient", out var valObj))
            {
                var val = valObj?.ToString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read AllowedRecipient from table storage. {ex.Message}");
        }

        // Fallback
        return "TestUserOne";
    }

    private static async Task<int> GetFirstPortAsync(TableServiceClient tableServiceClient)
    {
        const string SettingsTableName = "SMTPSettings";
        const string PartitionKey = "SmtpServer";
        const string RowKey = "Current";
        try
        {
            var table = tableServiceClient.GetTableClient(SettingsTableName);
            var entityResponse = await table.GetEntityAsync<TableEntity>(PartitionKey, RowKey);
            var entity = entityResponse.Value;
            // Prefer PortsJson
            if (entity.TryGetValue("PortsJson", out var portsJsonObj) && portsJsonObj is string portsJson && !string.IsNullOrWhiteSpace(portsJson))
            {
                try
                {
                    var ports = JsonSerializer.Deserialize<int[]>(portsJson) ?? Array.Empty<int>();
                    var first = ports.FirstOrDefault();
                    if (first > 0) return first;
                }
                catch { /* ignore parse errors */ }
            }
            if (entity.TryGetValue("Ports", out var portsCsvObj) && portsCsvObj is string portsCsv && !string.IsNullOrWhiteSpace(portsCsv))
            {
                var firstStr = portsCsv.Split(',').Select(s => s.Trim()).FirstOrDefault();
                if (int.TryParse(firstStr, out var p) && p > 0) return p;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read SMTP port from table storage, falling back to 2525. {ex.Message}");
        }
        return 2525; // fallback development port
    }

    private static async Task<(string? Username, string? Password)> GetCredentialsAsync(TableServiceClient tableServiceClient)
    {
        const string SettingsTableName = "SMTPSettings";
        const string PartitionKey = "SmtpServer";
        const string RowKey = "Current";
        try
        {
            var table = tableServiceClient.GetTableClient(SettingsTableName);
            var entityResponse = await table.GetEntityAsync<TableEntity>(PartitionKey, RowKey);
            var entity = entityResponse.Value;

            string? username = null;
            string? password = null;

            if (entity.TryGetValue("AllowedUsername", out var userObj))
            {
                username = userObj?.ToString();
            }
            if (entity.TryGetValue("AllowedPassword", out var passObj))
            {
                password = passObj?.ToString();
            }

            return (username, password);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read SMTP credentials from table storage. {ex.Message}");
            return (null, null);
        }
    }

    private static async Task<string> GetServerNameAsync(TableServiceClient tableServiceClient)
    {
        const string SettingsTableName = "SMTPSettings";
        const string PartitionKey = "SmtpServer";
        const string RowKey = "Current";
        try
        {
            var table = tableServiceClient.GetTableClient(SettingsTableName);
            var entityResponse = await table.GetEntityAsync<TableEntity>(PartitionKey, RowKey);
            var entity = entityResponse.Value;

            string servername = "";

            if (entity.TryGetValue("ServerName", out var serverObj))
            {
                servername = serverObj?.ToString() ?? "";
            }

            return (servername);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read ServerName from table storage. {ex.Message}");
            return ("");
        }
    }

    private static async Task SendTestEmail(TableServiceClient tableServiceClient, string smtpHost, string from, string to, string subject, string body, bool useAuth)
    {
        try
        {
            var port = await GetFirstPortAsync(tableServiceClient);
            using var client = new SmtpClient(smtpHost, port); // Use same host/IP as Blazor site

            if (useAuth)
            {
                var (username, password) = await GetCredentialsAsync(tableServiceClient);
                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    client.Credentials = new NetworkCredential(username, password);
                }
                else
                {
                    Console.WriteLine("Warning: Missing SMTP credentials in settings; proceeding without authentication.");
                }
            }

            client.EnableSsl = false;
            client.Timeout = 5000;

            var message = new MailMessage
            {
                From = new MailAddress(from),
                Subject = subject,
                Body = body
            };
            message.To.Add(to);

            await client.SendMailAsync(message);
            Console.WriteLine($"? Successfully sent email to {to} (Auth: {useAuth}, Host: {smtpHost}, Port: {port})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? Failed to send email to {to} (Auth: {useAuth}): {ex.Message}");
        }
    }

    public static async Task SendSpamTestEmail(TableServiceClient tableServiceClient, string smtpHost)
    {
        try
        {
            Console.WriteLine("Sending Spam Test Email...");
            var servername = await GetServerNameAsync(tableServiceClient);
            // Use the special sender address that triggers the test hook in SampleMailboxFilter
            await SendTestEmail(tableServiceClient, smtpHost, "spam-test@spamhaus.org", $"abuse@{servername}",
                "Spamhaus Trigger Test", "This email should trigger the Spamhaus detection test hook.", false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Spam Test failed: {ex.Message}");
        }
    }
}
