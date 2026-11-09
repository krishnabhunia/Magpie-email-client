using System.Net;
using System.Text.Json;
using Magpie.Core.CloudFiles;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    private const int MaximumCloudReceipts = 10000;

    private void EnsureCloudFilesSchema()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE IF NOT EXISTS cloud_file_receipts(web_url TEXT PRIMARY KEY, receipt TEXT NOT NULL)");
    }

    /// <summary>Persist non-secret access provenance independently of MIME/draft identity, so reopen and history cannot lose it.</summary>
    public void RememberCloudFile(CloudUploadedFile file)
    {
        if (!file.IsValid) throw new InvalidOperationException("The cloud file receipt is invalid.");
        var json = JsonSerializer.Serialize(file);
        if (json.Length > 65536) throw new InvalidOperationException("The cloud file receipt is too large.");
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM cloud_file_receipts")) >= MaximumCloudReceipts
            && Scalar(c, "SELECT 1 FROM cloud_file_receipts WHERE web_url=$url", ("$url", file.WebUrl)) == null)
            throw new InvalidOperationException("The cloud-file record limit was reached. Attach the file normally.");
        // URLs are immutable per fresh upload. Never let a different draft overwrite the original access proof.
        var existing = Scalar(c, "SELECT receipt FROM cloud_file_receipts WHERE web_url=$url", ("$url", file.WebUrl)) as string;
        if (existing != null && existing != json) throw new InvalidOperationException("The saved cloud file access differs from this upload.");
        Exec(c, "INSERT INTO cloud_file_receipts(web_url,receipt) VALUES($url,$json) ON CONFLICT(web_url) DO NOTHING",
            ("$url", file.WebUrl), ("$json", json));
        tx.Commit();
    }

    public IReadOnlyList<CloudUploadedFile> CloudFilesIn(string htmlOrText)
    {
        using var c = Open();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT receipt FROM cloud_file_receipts WHERE instr($body,web_url)>0 LIMIT 101";
        command.Parameters.AddWithValue("$body", WebUtility.HtmlDecode(htmlOrText));
        using var reader = command.ExecuteReader();
        var result = new List<CloudUploadedFile>();
        while (reader.Read())
        {
            var value = reader.GetString(0);
            var file = value.Length <= 65536 ? JsonSerializer.Deserialize<CloudUploadedFile>(value) : null;
            if (file?.IsValid != true) throw new InvalidOperationException("A saved cloud file record is damaged. Review the cloud links before sending.");
            result.Add(file);
        }
        if (result.Count > 100) throw new InvalidOperationException("This message contains too many tracked cloud links. Split the message before sending.");
        return result;
    }

    public void ForgetCloudFile(string webUrl)
    {
        using var c = Open();
        Exec(c, "DELETE FROM cloud_file_receipts WHERE web_url=$url", ("$url", webUrl));
    }
}
