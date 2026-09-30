using System;
using System.IO;

namespace HotReloadTool
{
    // Shares the already negotiated native package without handing binary team
    // traffic to the manager's JSON mod-download protocol.
    public static class TeamEnvelope
    {
        public static bool IsTeam(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3 && bytes[0] == 74 && bytes[1] == 67 && bytes[2] == 80;
        }
        public static byte[] Wrap(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 65532) throw new InvalidDataException("Team packet exceeds native package capacity");
            var result = new byte[bytes.Length + 4];
            result[0] = 74; result[1] = 67; result[2] = 80; result[3] = 1;
            Buffer.BlockCopy(bytes, 0, result, 4, bytes.Length);
            return result;
        }
        public static byte[] Unwrap(byte[] bytes)
        {
            if (!IsTeam(bytes) || bytes.Length < 4 || bytes[3] != 1) return null;
            var result = new byte[bytes.Length - 4];
            Buffer.BlockCopy(bytes, 4, result, 0, result.Length);
            return result;
        }
    }
}
