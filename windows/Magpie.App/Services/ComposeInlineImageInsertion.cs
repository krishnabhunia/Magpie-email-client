using Magpie.App.ViewModels;

namespace Magpie.App.Services;

/// <summary>Keep a file-picker picture operation owned until its bytes and editor insertion have both settled.</summary>
public static class ComposeInlineImageInsertion
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    public static async Task<bool> InsertAsync(
        ComposeViewModel compose,
        string path,
        Func<string, Task<bool>> insertImage,
        Func<bool> editorAvailable,
        Func<string, Task<byte[]>>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(compose);
        ArgumentNullException.ThrowIfNull(insertImage);
        ArgumentNullException.ThrowIfNull(editorAvailable);
        if (compose.IsPlainText || !editorAvailable() || !compose.TryBeginAttachmentOperation()) return false;
        try
        {
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                _ => throw new InvalidDataException("Choose a PNG, JPG or GIF image."),
            };
            // The claim precedes the first await, including slow network-share reads.
            var bytes = await (readFile ?? ReadBoundedAsync)(path);
            if (bytes.Length == 0) throw new InvalidDataException("This picture file is empty.");
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("This picture is larger than 8 MB. Reduce its size or attach it as a file.");
            if (!compose.CanCompleteAttachmentOperation || compose.IsPlainText || !editorAvailable())
            {
                compose.Status = "The editor changed while the picture was loading. Insert the picture again when it is ready.";
                return false;
            }
            var inserted = await insertImage("data:" + mime + ";base64," + Convert.ToBase64String(bytes));
            if (!inserted)
            {
                compose.Status = "The picture was not inserted. Click in the message and try again.";
                return false;
            }
            if (!compose.CanCompleteAttachmentOperation || compose.IsPlainText || !editorAvailable()) return false;
            // The DOM acknowledgement has completed before releasing Send/Close protection.
            // Do not rely on a separately dispatched WebView dirty notification to mark the edit.
            compose.MarkEdited();
            return true;
        }
        finally
        {
            compose.AttachmentOperationBusy = false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaximumBytes)
            throw new InvalidDataException("This picture is larger than 8 MB. Reduce its size or attach it as a file.");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory())) != 0)
        {
            if (output.Length + count > MaximumBytes)
                throw new InvalidDataException("This picture grew beyond the 8 MB size limit while it was loading.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
