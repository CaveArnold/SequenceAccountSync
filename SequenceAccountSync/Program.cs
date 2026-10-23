/*
 * Developer: Cave Arnold
 * AI Assistant: Gemini (Google)
 * Date: 2026-08-23
 * Version: 1.0.1
 * 
 * Logic Abstraction:
 * - Parses command-line arguments for API token, sync interval, and optional SMTP settings.
 * - Runs an infinite loop with a configurable delay between synchronization passes.
 * - Fetches account data from the Sequence API (hardcoded to page 1, state parameter removed).
 * - Retrieves detailed information for each account using its specific lowercase ID.
 * - Compares incoming API data against existing SQL Server database records to explicitly log specific field changes (To/From).
 * - Detects changes in the balance (BalanceInCents) and records historical snapshots in the [Balances] table.
 * - Sends an email alert via SMTP if any account balances have changed.
 * - Uses an advanced SQL MERGE statement to efficiently insert or update the main [SequenceAccounts] table only when data changes.
 * - Ignores LinkedAccount field in data processing and database operations.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.Data.SqlClient;
using RestSharp;
using Serilog;

namespace SequenceAccountSync
{
    public class Options
    {
        [Option('t', "token", Required = true, HelpText = "Bearer access token for the API.")]
        public string AccessToken { get; set; }

        [Option('n', "interval", Required = true, HelpText = "Interval in seconds between checks.")]
        public int IntervalSeconds { get; set; }

        [Option("smtp-server", Required = false, HelpText = "SMTP Server Address (Optional).")]
        public string SmtpServer { get; set; }

        [Option("smtp-port", Required = false, Default = 587, HelpText = "SMTP Port (Optional).")]
        public int SmtpPort { get; set; }

        [Option("email-from", Required = false, HelpText = "Email Sender Address (Optional).")]
        public string EmailFrom { get; set; }

        [Option("email-to", Required = false, HelpText = "Email Recipient Address (Optional).")]
        public string EmailTo { get; set; }

        [Option("smtp-user", Required = false, HelpText = "SMTP Username.")]
        public string SmtpUser { get; set; }

        [Option("smtp-pass", Required = false, HelpText = "SMTP Password.")]
        public string SmtpPass { get; set; }

        [Option("enable-ssl", Required = false, Default = false, HelpText = "Enable SSL for SMTP.")]
        public bool EnableSsl { get; set; }
    }

    class Program
    {
        // Database Connection String
        private const string ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=Guyton-Klinger-Withdrawals;Integrated Security=True;TrustServerCertificate=True;";
        private const string BaseUrl = "https://api.getsequence.io/platform/v1";

        static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("logs/sequence-sync-.txt", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
                .CreateLogger();

            Log.Information("=========================================================");
            Log.Information("Sequence Account Monitor Starting...");

            // Parse command line arguments
            await Parser.Default.ParseArguments<Options>(args)
                .WithParsedAsync(RunAppAsync);

            Log.Information("Application exiting...");
            Log.CloseAndFlush();
        }

        static async Task RunAppAsync(Options opts)
        {
            var options = new RestClientOptions(BaseUrl); // MaxTimeout removed

            using var client = new RestClient(options);
            client.AddDefaultHeader("Authorization", $"Bearer {opts.AccessToken}");
            client.AddDefaultHeader("Accept", "application/json");
            client.AddDefaultHeader("x-called-reason", "C# Application to Synchronize Sequence Accounts.");

            // Enforce minimum 1-second delay
            int delayMilliseconds = Math.Max(1000, opts.IntervalSeconds * 1000);

            // Infinite Loop
            while (true)
            {
                try
                {
                    Log.Information("--- Starting pass ---");
                    var detailedAccounts = new List<SequenceAccount>();
                    int totalAccountsProcessed = 0;
                    var emailMessages = new List<string>();

                    // Limited to Page 1 only
                    for (int currentPage = 1; currentPage <= 3; currentPage++)
                    {
                        var request = new RestRequest("accounts/", Method.Get);
                        request.AddQueryParameter("pageSize", "50");
                        request.AddQueryParameter("includeDeleted", "true");
                        // state=ALL parameter removed
                        request.AddQueryParameter("page", currentPage.ToString());

                        // Wait before next pass
                        await Task.Delay(delayMilliseconds);

                        var response = await client.ExecuteAsync<ApiResponse>(request);

                        if (!response.IsSuccessful)
                        {
                            Log.Error("API call failed on page {CurrentPage}. Status {StatusCode}: {ErrorMessage}", currentPage, response.StatusCode, response.ErrorMessage);
                            break;
                        }

                        List<SequenceAccount> accountList = response.Data?.Data?.Items ?? response.Data?.Data?.Accounts;

                        if (accountList != null && accountList.Count > 0)
                        {
                            foreach (var account in accountList)
                            {
                                string lowerId = account.Id.ToString("D").ToLower();
                                var detailRequest = new RestRequest($"accounts/{lowerId}", Method.Get);
                                var detailResponse = await client.ExecuteAsync<SingleApiResponse>(detailRequest);

                                if (detailResponse.IsSuccessful && detailResponse.Data?.Data != null)
                                {
                                    var acc = detailResponse.Data.Data;
                                    detailedAccounts.Add(acc);

                                    // Process Balance History and Email Alerts
                                    decimal? currentBalance = null;
                                    if (acc.Balance?.BalanceInCents != null)
                                    {
                                        currentBalance = acc.Balance.BalanceInCents.Value / 100m; // Convert Cents to Decimal
                                    }

                                    decimal? lastBalance = GetLastBalanceFromDb(lowerId);

                                    // Check if the balance changed (or is being recorded for the very first time)
                                    if (currentBalance != lastBalance)
                                    {
                                        Log.Information("Balance history triggered for {AccountName}: Old={OldBalance}, New={NewBalance}", acc.Name, lastBalance, currentBalance);
                                        InsertIntoDatabase(acc, currentBalance);

                                        emailMessages.Add($"Account: {acc.Name}\nType: {acc.Type}\nOld Balance: {lastBalance:C}\nNew Balance: {currentBalance:C}");
                                    }
                                }
                            }
                            totalAccountsProcessed += detailedAccounts.Count;
                        }
                    }

                    if (detailedAccounts.Count > 0)
                    {
                        // Explicitly compare and log field-level changes before merging
                        LogFieldLevelChanges(detailedAccounts);

                        // Push core data changes to SequenceAccounts via MERGE
                        await SaveAccountsToDatabase(detailedAccounts);
                    }

                    // Send Email if there were balance changes
                    if (emailMessages.Count > 0)
                    {
                        SendEmail(emailMessages, opts);
                    }

                    Log.Information("--- Pass complete. Waiting {Seconds} seconds. ---", delayMilliseconds / 1000);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An error occurred during the synchronization loop. Retrying on next pass.");
                }

                // Wait before next pass
                await Task.Delay(delayMilliseconds);
            }
        }

        // --- Field Level Change Logging ---

        private static void LogFieldLevelChanges(List<SequenceAccount> incomingAccounts)
        {
            try
            {
                using var conn = new SqlConnection(ConnectionString);
                conn.Open();

                foreach (var incoming in incomingAccounts)
                {
                    string sql = "SELECT * FROM SequenceAccounts WHERE Id = @Id";
                    using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@Id", incoming.Id.ToString("D").ToLower());

                    using var reader = cmd.ExecuteReader();
                    string accountName = incoming.Name ?? incoming.Id.ToString();

                    if (reader.Read())
                    {
                        // Compare core fields
                        CompareAndLog("Name", reader["Name"], incoming.Name, accountName);
                        CompareAndLog("Type", reader["Type"], incoming.Type, accountName);
                        CompareAndLog("Description", reader["Description"], incoming.Description, accountName);
                        CompareAndLog("RoutingNumber", reader["RoutingNumber"], incoming.RoutingNumber, accountName);
                        CompareAndLog("BankAccountNumber", reader["BankAccountNumber"], incoming.BankAccountNumber, accountName);
                        CompareAndLog("ExternalAccountType", reader["ExternalAccountType"], incoming.ExternalAccountType, accountName);
                        CompareAndLog("BeneficiaryName", reader["BeneficiaryName"], incoming.BeneficiaryName, accountName);
                        CompareAndLog("InstitutionName", reader["InstitutionName"], incoming.InstitutionName, accountName);

                        // Compare Balance subset
                        CompareAndLog("BalanceInCents", reader["BalanceInCents"], incoming.Balance?.BalanceInCents, accountName);
                        CompareAndLog("AvailableBalanceInCents", reader["AvailableBalanceInCents"], incoming.Balance?.AvailableBalanceInCents, accountName);
                        CompareAndLog("LastStatementBalanceInCents", reader["LastStatementBalanceInCents"], incoming.Balance?.LastStatementBalanceInCents, accountName);
                        //CompareAndLog("InterestRatePercentage", reader["InterestRatePercentage"], incoming.Balance?.InterestRatePercentage, accountName);
                        CompareAndLog("OriginalLoanAmountInCents", reader["OriginalLoanAmountInCents"], incoming.Balance?.OriginalLoanAmountInCents, accountName);
                        CompareAndLog("Error", reader["Error"], incoming.Balance?.Error, accountName);
                    }
                    else
                    {
                        Log.Information("New Account Detected in API stream: '{AccountName}' ({Id})", accountName, incoming.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to analyze field-level changes prior to database merge.");
            }
        }

        private static void CompareAndLog(string fieldName, object dbValue, object newValue, string accountName)
        {
            if (dbValue == DBNull.Value) dbValue = null;

            // Handle date types to avoid string formatting false-positives
            if (dbValue is DateTimeOffset dbDate && newValue is DateTimeOffset newDate)
            {
                if (dbDate != newDate)
                    Log.Information("Account '{AccountName}': {Field} changed from '{Old}' to '{New}'", accountName, fieldName, dbDate, newDate);
                return;
            }

            string dbStr = dbValue?.ToString() ?? "NULL";
            string newStr = newValue?.ToString() ?? "NULL";

            if (dbStr != newStr)
            {
                Log.Information("Account '{AccountName}': {Field} changed from '{Old}' to '{New}'", accountName, fieldName, dbStr, newStr);
            }
        }

        // --- Balance Tracking Logic ---

        private static decimal? GetLastBalanceFromDb(string sequenceId)
        {
            try
            {
                using var conn = new SqlConnection(ConnectionString);
                conn.Open();
                string sql = "SELECT TOP 1 Balance FROM Balances_Testing WHERE SequenceID = @SeqId ORDER BY LastUpdate DESC";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@SeqId", sequenceId);
                var result = cmd.ExecuteScalar();

                if (result != null && result != DBNull.Value)
                {
                    return (decimal)result;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to retrieve last balance from DB for {SequenceId}.", sequenceId);
            }
            return null;
        }

        private static void InsertIntoDatabase(SequenceAccount acc, decimal? roundedBalance)
        {
            try
            {
                using var conn = new SqlConnection(ConnectionString);
                conn.Open();
                string sql = @"
                    INSERT INTO [dbo].[Balances_Testing] 
                    ([SequenceID], [Name], [Balance], [Type], [Error], [LastUpdate])
                    VALUES 
                    (@SeqId, @Name, @Balance, @Type, @Error, @LastUpdate)";

                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@SeqId", acc.Id.ToString("D").ToLower());
                cmd.Parameters.AddWithValue("@Name", acc.Name ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Balance", roundedBalance ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Type", acc.Type ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Error", acc.Balance?.Error ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@LastUpdate", DateTime.Now);

                cmd.ExecuteNonQuery();
                Log.Debug("Database record inserted for {AccountName}", acc.Name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Database insert failed for {AccountName}", acc.Name);
            }
        }

        // --- SMTP Email Alerting ---

        private static void SendEmail(List<string> messages, Options opts)
        {
            if (string.IsNullOrWhiteSpace(opts.SmtpServer))
            {
                Log.Information("SMTP Server not provided. Skipping email alert.");
                return;
            }

            try
            {
                using var smtp = new SmtpClient(opts.SmtpServer, opts.SmtpPort);
                smtp.EnableSsl = opts.EnableSsl;
                Log.Debug("SMTP Enable SSL: {EnableSsL}", opts.EnableSsl);

                smtp.DeliveryMethod = SmtpDeliveryMethod.Network;

                if (!string.IsNullOrWhiteSpace(opts.SmtpUser) && !string.IsNullOrWhiteSpace(opts.SmtpPass))
                {
                    smtp.Credentials = new NetworkCredential(opts.SmtpUser, opts.SmtpPass);
                }

                var mail = new MailMessage
                {
                    From = new MailAddress(opts.EmailFrom),
                    Subject = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + " - " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + " - " + DateTime.Now.ToString("MM/dd/yyyy HH:mm:ss"),
                    Body = string.Join(Environment.NewLine + Environment.NewLine, messages)
                };

                mail.To.Add(opts.EmailTo);

                smtp.Send(mail);
                Log.Information("Email alert successfully sent via {SmtpServer}", opts.SmtpServer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send email alert.");
            }
        }

        // --- Main Table Merge Synchronization ---

        static async Task<int> SaveAccountsToDatabase(List<SequenceAccount> accounts)
        {
            int totalRowsAffected = 0;
            string mergeQuery = @"
                MERGE INTO SequenceAccounts AS target
                USING (SELECT @Id AS Id, @Name AS Name, @Type AS Type, @Description AS Description, 
                              @ExternalAccountType AS ExternalAccountType, @BeneficiaryName AS BeneficiaryName, 
                              @InstitutionName AS InstitutionName, @CanBeSource AS CanBeSource, 
                              @CanBeDestination AS CanBeDestination, 
                              @CreatedAt AS CreatedAt, @UpdatedAt AS UpdatedAt, @DeletedAt AS DeletedAt,
                              @RoutingNumber AS RoutingNumber, @BankAccountNumber AS BankAccountNumber,
                              @BalanceInCents AS BalanceInCents, @AvailableBalanceInCents AS AvailableBalanceInCents,
                              @LastStatementBalanceInCents AS LastStatementBalanceInCents, @LastStatementDate AS LastStatementDate,
                              @NextPaymentMinimumInCents AS NextPaymentMinimumInCents, @NextPaymentDueDate AS NextPaymentDueDate,
                              @BalanceLastUpdatedAt AS BalanceLastUpdatedAt, @Error AS Error, 
                              @InterestRatePercentage AS InterestRatePercentage, @OriginalLoanAmountInCents AS OriginalLoanAmountInCents,
                              @SavingsTargetInCents AS SavingsTargetInCents) AS source
                ON target.Id = source.Id
                WHEN MATCHED AND NOT EXISTS (
                    SELECT target.Name, target.Type, target.Description, target.ExternalAccountType, target.BeneficiaryName, 
                           target.InstitutionName, target.CanBeSource, target.CanBeDestination, 
                           target.UpdatedAt, target.DeletedAt, target.RoutingNumber, target.BankAccountNumber, 
                           target.BalanceInCents, target.AvailableBalanceInCents, target.LastStatementBalanceInCents, 
                           target.LastStatementDate, target.NextPaymentMinimumInCents, target.NextPaymentDueDate, 
                           target.BalanceLastUpdatedAt, target.Error, target.InterestRatePercentage, 
                           target.OriginalLoanAmountInCents, target.SavingsTargetInCents
                    INTERSECT
                    SELECT source.Name, source.Type, source.Description, source.ExternalAccountType, source.BeneficiaryName, 
                           source.InstitutionName, source.CanBeSource, source.CanBeDestination, 
                           source.UpdatedAt, source.DeletedAt, source.RoutingNumber, source.BankAccountNumber, 
                           source.BalanceInCents, source.AvailableBalanceInCents, source.LastStatementBalanceInCents, 
                           source.LastStatementDate, source.NextPaymentMinimumInCents, source.NextPaymentDueDate, 
                           source.BalanceLastUpdatedAt, source.Error, source.InterestRatePercentage, 
                           source.OriginalLoanAmountInCents, source.SavingsTargetInCents
                ) THEN 
                    UPDATE SET Name = source.Name, Type = source.Type, Description = source.Description,
                               ExternalAccountType = source.ExternalAccountType, BeneficiaryName = source.BeneficiaryName,
                               InstitutionName = source.InstitutionName, CanBeSource = source.CanBeSource,
                               CanBeDestination = source.CanBeDestination, 
                               CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt, DeletedAt = source.DeletedAt,
                               RoutingNumber = source.RoutingNumber, BankAccountNumber = source.BankAccountNumber,
                               BalanceInCents = source.BalanceInCents, AvailableBalanceInCents = source.AvailableBalanceInCents,
                               LastStatementBalanceInCents = source.LastStatementBalanceInCents, LastStatementDate = source.LastStatementDate,
                               NextPaymentMinimumInCents = source.NextPaymentMinimumInCents, NextPaymentDueDate = source.NextPaymentDueDate,
                               BalanceLastUpdatedAt = source.BalanceLastUpdatedAt, Error = source.Error,
                               InterestRatePercentage = source.InterestRatePercentage, OriginalLoanAmountInCents = source.OriginalLoanAmountInCents,
                               SavingsTargetInCents = source.SavingsTargetInCents,
                               LastUpdate = SYSDATETIMEOFFSET()
                WHEN NOT MATCHED THEN
                    INSERT (Id, Name, Type, Description, ExternalAccountType, BeneficiaryName, InstitutionName, 
                            CanBeSource, CanBeDestination, CreatedAt, UpdatedAt, DeletedAt,
                            RoutingNumber, BankAccountNumber, BalanceInCents, AvailableBalanceInCents, LastStatementBalanceInCents,
                            LastStatementDate, NextPaymentMinimumInCents, NextPaymentDueDate, BalanceLastUpdatedAt, Error,
                            InterestRatePercentage, OriginalLoanAmountInCents, SavingsTargetInCents)
                    VALUES (source.Id, source.Name, source.Type, source.Description, source.ExternalAccountType, 
                            source.BeneficiaryName, source.InstitutionName, source.CanBeSource, source.CanBeDestination, 
                            source.CreatedAt, source.UpdatedAt, source.DeletedAt,
                            source.RoutingNumber, source.BankAccountNumber, source.BalanceInCents, source.AvailableBalanceInCents, 
                            source.LastStatementBalanceInCents, source.LastStatementDate, source.NextPaymentMinimumInCents, 
                            source.NextPaymentDueDate, source.BalanceLastUpdatedAt, source.Error, source.InterestRatePercentage, 
                            source.OriginalLoanAmountInCents, source.SavingsTargetInCents);";

            using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            foreach (var account in accounts)
            {
                using var command = new SqlCommand(mergeQuery, connection);

                command.Parameters.AddWithValue("@Id", account.Id.ToString("D").ToLower());
                command.Parameters.AddWithValue("@Name", account.Name ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Type", account.Type ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Description", account.Description ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@ExternalAccountType", account.ExternalAccountType ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@BeneficiaryName", account.BeneficiaryName ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@InstitutionName", account.InstitutionName ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@CanBeSource", account.CanBeSource);
                command.Parameters.AddWithValue("@CanBeDestination", account.CanBeDestination);
                command.Parameters.AddWithValue("@CreatedAt", account.CreatedAt);
                command.Parameters.AddWithValue("@UpdatedAt", account.UpdatedAt);
                command.Parameters.AddWithValue("@DeletedAt", account.DeletedAt.HasValue ? (object)account.DeletedAt.Value : DBNull.Value);
                command.Parameters.AddWithValue("@RoutingNumber", account.RoutingNumber ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@BankAccountNumber", account.BankAccountNumber ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@SavingsTargetInCents", account.SavingsTargetInCents ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@BalanceInCents", account.Balance?.BalanceInCents ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@AvailableBalanceInCents", account.Balance?.AvailableBalanceInCents ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@LastStatementBalanceInCents", account.Balance?.LastStatementBalanceInCents ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@LastStatementDate", account.Balance?.LastStatementDate ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@NextPaymentMinimumInCents", account.Balance?.NextPaymentMinimumInCents ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@NextPaymentDueDate", account.Balance?.NextPaymentDueDate ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@BalanceLastUpdatedAt", account.Balance?.BalanceLastUpdatedAt ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Error", account.Balance?.Error ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@InterestRatePercentage", account.Balance?.InterestRatePercentage ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@OriginalLoanAmountInCents", account.Balance?.OriginalLoanAmountInCents ?? (object)DBNull.Value);

                totalRowsAffected += await command.ExecuteNonQueryAsync();
            }

            return totalRowsAffected;
        }
    }

    // --- API and Data Models ---

    public class ApiResponse
    {
        public DataPayload Data { get; set; }
        public PaginationSettings Pagination { get; set; }
    }

    public class SingleApiResponse
    {
        public SequenceAccount Data { get; set; }
    }

    public class DataPayload
    {
        public List<SequenceAccount> Accounts { get; set; }
        public List<SequenceAccount> Items { get; set; }
    }

    public class PaginationSettings
    {
        public bool HasNextPage { get; set; }
    }

    public class SequenceAccount
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public string ExternalAccountType { get; set; }
        public string BeneficiaryName { get; set; }
        public string InstitutionName { get; set; }
        public bool CanBeSource { get; set; }
        public bool CanBeDestination { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public string RoutingNumber { get; set; }
        public string BankAccountNumber { get; set; }
        public long? SavingsTargetInCents { get; set; }
        public AccountBalance Balance { get; set; }
    }

    public class AccountBalance
    {
        public long? BalanceInCents { get; set; }
        public long? AvailableBalanceInCents { get; set; }
        public long? LastStatementBalanceInCents { get; set; }
        public DateTimeOffset? LastStatementDate { get; set; }
        public long? NextPaymentMinimumInCents { get; set; }
        public DateTimeOffset? NextPaymentDueDate { get; set; }
        public DateTimeOffset? BalanceLastUpdatedAt { get; set; }
        public string Error { get; set; }
        public decimal? InterestRatePercentage { get; set; }
        public long? OriginalLoanAmountInCents { get; set; }
    }
}