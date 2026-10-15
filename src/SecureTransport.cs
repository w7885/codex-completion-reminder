using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CodexCompletionReminder
{
    internal static class SecureTransport
    {
        public const int MaxHookChars = 65536;
        public const int MaxMessageBytes = 512;
        public const int MaxIdChars = 128;
        // Keep the app, installed Hook and Startup entry on the same V4 transport.
        // The pipe ACL below still grants access exclusively to the current user.
        public static string PipeName { get { return "CodexCompletionReminderV4"; } }
        public static PipeSecurity Security()
        {
            PipeSecurity acl = new PipeSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(WindowsIdentity.GetCurrent().User);
            acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return acl;
        }
        public static NamedPipeServerStream CreateServer()
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, Security());
        }
        public static string ReadBounded(TextReader reader, int maximum)
        {
            char[] buffer = new char[maximum + 1];
            int count = 0, read;
            while (count < buffer.Length && (read = reader.Read(buffer, count, buffer.Length - count)) > 0) count += read;
            if (count > maximum) throw new InvalidDataException("input-too-long");
            return new string(buffer, 0, count);
        }
        public static bool ValidId(string id)
        {
            if (String.IsNullOrEmpty(id) || id.Length > MaxIdChars) return false;
            foreach (char c in id)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '-' && c != '_' && c != '.' && c != ':') return false;
            return true;
        }
        public static string ReadMessage(Stream stream)
        {
            // One byte at a time, bounded and timed: a connected silent writer cannot block shutdown.
            using (MemoryStream buffer = new MemoryStream())
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(700);
                byte[] one = new byte[1];
                while (buffer.Length < MaxMessageBytes)
                {
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0) throw new IOException("message-timeout");
                    System.Threading.Tasks.Task<int> read = stream.ReadAsync(one, 0, 1);
                    if (!read.Wait(remaining)) throw new IOException("message-timeout");
                    if (read.Result == 0) throw new InvalidDataException("unterminated-message");
                    if (one[0] == 10) return new UTF8Encoding(false, true).GetString(buffer.ToArray());
                    buffer.WriteByte(one[0]);
                }
                throw new InvalidDataException("message-too-long");
            }
        }
    }
}
