# BlazorSMTPForwarder
### Forward unlimited domains through SendGrid
## Blog Post: [Deploy Your Own Unlimited Forwarding To SendGrid SMTP Server](https://blazorhelpwebsite.com/ViewBlogPost/20082)
## Also see: [https://github.com/ADefWebserver/BlazorSMTPServer](https://github.com/ADefWebserver/BlazorSMTPServer)

<img width="562" height="515" alt="image" src="https://github.com/user-attachments/assets/89bfb145-b21c-4e3f-b575-d8573b9d54dc" />

## Features

*   **Unlimited Domain Forwarding**  
    Configure and manage email forwarding for an unlimited number of domains from a single instance.

*   **SendGrid Integration**  
    Leverages the SendGrid API for reliable, high-deliverability email forwarding without managing complex SMTP relay infrastructure.

*   **Advanced Routing Rules**  
    *   **Direct Forwarding:** Map specific incoming email addresses to destination addresses.
    *   **Catch-All Support:** Configure catch-all forwarding to handle any email sent to a domain that doesn't match a specific rule.

*   **Web-Based Administration**  
    *   **Dashboard:** A modern Blazor-based web interface to manage all settings and domains.
    *   **Inbox Viewer:** View received and archived emails directly in the browser.
    *   **Log Viewer:** Built-in interface to view server logs for monitoring and troubleshooting.

*   **Robust Anti-Spam Protection**  
    *   Integrated **SPF**, **DKIM**, and **DMARC** verification.
    *   **Spamhaus RBL** (Real-time Blackhole List) integration to block known spam sources.

*   **Azure Cloud Native**  
    *   **Azure Table Storage:** Uses Table Storage for scalable, low-cost configuration and logging.
    *   **Azure Blob Storage:** Archives all received emails as `.eml` files in Blob Storage for backup and auditing.

*   **Secure Access**  
    The web administration interface is protected by a configurable application password to prevent unauthorized access.

<img width="777" height="593" alt="image" src="https://github.com/user-attachments/assets/1f38dc5d-14f1-4008-ae25-b1b978446048" />

<img width="708" height="382" alt="image" src="https://github.com/user-attachments/assets/446ed393-7721-4ca9-9559-7a506c8e982c" />

<img width="727" height="554" alt="image" src="https://github.com/user-attachments/assets/80a56865-c9d2-4785-8c1e-c1851d712397" />

<img width="736" height="416" alt="image" src="https://github.com/user-attachments/assets/b0c39bd3-b404-4954-9164-bcbd06054fb1" />

<img width="738" height="376" alt="image" src="https://github.com/user-attachments/assets/61db26c4-850a-4732-84da-54afc43fe7e0" />

<img width="742" height="394" alt="image" src="https://github.com/user-attachments/assets/829b7304-1f00-4d49-a968-005e789e62d1" />


```mermaid
sequenceDiagram
    participant Program
    participant HostedService as SmtpServerHostedService
    participant Zetian as Zetian.Server (SmtpServer)
    participant Handler as ZetianMessageHandler
    participant Table as Azure Table Storage
    participant Blob as Azure Blob Storage
    participant SendGrid as SendGrid API
    participant Client as External SMTP Client

    Note over Program, HostedService: Application Startup
    Program->>HostedService: StartAsync()
    
    Note over HostedService, Table: Load Configuration
    HostedService->>Table: LoadSettingsAsync()
    Table-->>HostedService: SMTPSettings (spam, domains, SendGrid key)
    
    Note over HostedService, Zetian: Configure SMTP Server
    HostedService->>Zetian: SmtpServerBuilder().ServerName().Port().Build()
    HostedService->>Zetian: AddAntiSpam(SPF, DKIM, DMARC, RBL)
    HostedService->>Zetian: MessageReceived += Handler.HandleMessageAsync
    HostedService->>Zetian: StartAsync()
    activate Zetian

    Note over Client, Zetian: Email Transmission
    Client->>Zetian: Connect & Send Email
    
    Note right of Zetian: Anti-Spam Pipeline<br/>(SPF, DKIM, DMARC, RBL checks)
    
    Zetian->>Handler: MessageReceived event
    activate Handler

    Note over Handler, Blob: Message Processing
    Handler->>Handler: Check if Recipients are Local/Configured
    
    alt Has Configured Recipients
        opt Save to Blob Enabled
            Handler->>Blob: CreateIfNotExistsAsync(email-messages)
            Handler->>Blob: Upload .eml file
            Handler-->>Handler: Log "Message saved to blob"
        end
        
        loop For Each Recipient
            Handler->>Handler: Check Forwarding Rules
            alt Rule Exists
                Handler->>SendGrid: SendEmailAsync (Forward Message)
                SendGrid-->>Handler: 202 Accepted / Error
                Handler-->>Handler: Log Result
            end
        end
    end

    Handler-->>Zetian: Complete
    deactivate Handler

    Zetian-->>Client: SMTP Response (250 OK / Error)
    
    Note over Program, HostedService: Application Shutdown
    Program->>HostedService: StopAsync()
    HostedService->>Zetian: StopAsync()
    deactivate Zetian
```
