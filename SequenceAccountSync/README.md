# Sequence Account Sync

**Developer:** Cave Arnold  
**Version:** 1.1.0.0  

Sequence Account Sync is a C# console application designed to monitor and synchronize account data from the [Sequence API](https://api.getsequence.io/) into a local SQL Server database. It tracks field-level changes, records historical balance snapshots, and can send SMTP email alerts whenever an account balance changes.

## Features

* **Continuous Monitoring:** Runs in an infinite loop with a configurable delay between synchronization passes.
* **API Integration:** Fetches account lists and detailed information using the Sequence REST API.
* **Smart Data Merging:** Uses an advanced SQL `MERGE` statement to efficiently insert or update records in the main database table only when underlying data has changed.
* **Balance Tracking:** Detects changes in `BalanceInCents` and inserts historical snapshots into a dedicated tracking table.
* **Email Alerts:** Automatically sends email notifications via SMTP when balance changes are detected.
* **Custom Data Processing:** Includes custom intercepts (e.g., renaming specific accounts like "Discover More" for enhanced UI/tracking).
* **Detailed Logging:** Utilizes Serilog for daily rolling log files, tracking both API errors and specific field-level data changes (Old Value vs. New Value).

## Prerequisites

* [.NET SDK](https://dotnet.microsoft.com/download) (Version 6.0 or higher recommended)
* SQL Server LocalDB (or standard SQL Server, requires updating the connection string)
* Access to the Sequence API (Bearer Token)

### NuGet Dependencies
The project relies on the following NuGet packages:
* `CommandLineParser`
* `Microsoft.Data.SqlClient`
* `RestSharp`
* `Serilog`
* `Serilog.Sinks.File`

## Database Setup

The application expects a SQL Server database named `Guyton-Klinger-Withdrawals` (by default on `(localdb)\MSSQLLocalDB`). You will need two primary tables:
1. `SequenceAccounts`: Stores the main account data (mapped to the SQL MERGE statement).
2. `Balances_Testing`: Stores the historical balance snapshots.

*(Note: The connection string is currently hardcoded in `Program.cs` and can be adjusted as needed).*

## Usage

Run the application via the command line, providing the required arguments for your API token and polling interval.

```bash
SequenceAccountSync.exe -t "<YOUR_BEARER_TOKEN>" -n <INTERVAL_IN_SECONDS> [OPTIONS]
```

### Command Line Arguments

| Short | Long | Required | Description |
| :---: | :--- | :---: | :--- |
| `-t` | `--token` | **Yes** | Bearer access token for the Sequence API. |
| `-n` | `--interval` | **Yes** | Interval in seconds between API checks. |
| | `--smtp-server` | No | SMTP Server Address (Required if you want email alerts). |
| | `--smtp-port` | No | SMTP Port (Default: 587). |
| | `--email-from` | No | Email Sender Address. |
| | `--email-to` | No | Email Recipient Address. |
| | `--smtp-user` | No | SMTP Username for authentication. |
| | `--smtp-pass` | No | SMTP Password for authentication. |
| | `--enable-ssl` | No | Enable SSL for SMTP (Default: false). |

### Example

Start syncing every 60 seconds with email alerts enabled:

```bash
SequenceAccountSync.exe -t "abc123xyz" -n 60 --smtp-server "smtp.example.com" --email-from "alerts@example.com" --email-to "myemail@example.com" --smtp-user "alerts@example.com" --smtp-pass "MySecretPass" --enable-ssl
```

## Logging

Logs are written to the `logs/` directory relative to the executable. A new log file is created daily (`sequence-sync-YYYYMMDD.txt`), and the application retains the last 30 days of logs by default.