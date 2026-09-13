using System.Security.Cryptography;
using System.Text;

namespace Contracts;

// One logical command = one CommandId, for ever. A retry after a timeout gets a new
// transport MessageId (so the broker and Wolverine's inbox treat it as new) but the
// SAME CommandId, which is what participants and FakePay dedupe on. Deriving it from
// (SagaId, Step, Direction) means a restarted orchestrator re-derives the same id
// without having stored it: no GetHashCode (per-process), no enum ordinals (reorderable).
public static class CommandId
{
    public static Guid For(Guid sagaId, string step, Direction direction)
    {
        var input = $"{sagaId:D}:{step}:{(direction == Direction.Forward ? "fwd" : "comp")}";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);

        var bytes = hash[..16];
        // RFC 9562 version 8 (custom) layout, so the value is a well-formed GUID.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
