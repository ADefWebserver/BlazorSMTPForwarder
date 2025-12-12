# BlazorSMTPForwarderSrv Code Flow

This diagram illustrates the startup and message processing flow of the SMTP Forwarder Service.

```mermaid
sequenceDiagram
    participant Program
    participant HostedService as SmtpServerHostedService
    participant SmtpLib as SmtpServer (Library)
    participant Store as DefaultMessageStore
    participant Table as Azure Table Storage
    participant Blob as Azure Blob Storage
    participant Client as External SMTP Client

    Note over Program, HostedService: Application Startup
    Program->>HostedService: StartAsync()
    HostedService->>SmtpLib: Configure (Ports, Name)
    HostedService->>SmtpLib: StartAsync()
    activate SmtpLib

    Note over Client, SmtpLib: Email Transmission
    Client->>SmtpLib: Connect & Send Email
    SmtpLib->>Store: SaveAsync(context, transaction)
    activate Store

    Note over Store, Blob: Message Processing
    Store->>Store: EnsureResourcesAsync()
    Store->>Blob: Create Container (email-messages) if not exists
    Store->>Table: Create Table (spamlogs) if not exists
    
    Store->>Store: RefreshSettingsAsync()
    Store->>Table: Fetch Settings (SMTPSettings)
    
    Note right of Store: Perform Checks (Spamhaus, SPF, DKIM, DMARC)<br/>based on settings
    
    alt Is Valid Message
        Store->>Blob: Upload Email Content
        opt Relay Enabled
            Store->>Store: Relay Message (if configured)
        end
    else Is Spam/Rejected
        Store->>Table: Log to 'spamlogs'
    end

    Store-->>SmtpLib: Response (OK/Failure)
    deactivate Store

    SmtpLib-->>Client: SMTP Response (250 OK / Error)
    
    Note over Program, HostedService: Application Shutdown
    Program->>HostedService: StopAsync()
    HostedService->>SmtpLib: Shutdown()
    deactivate SmtpLib
```
