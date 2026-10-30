using MessagePack;
using MessagePack.Resolvers;

namespace NeuroViz.Core.Serialization;

public static class MessagePackOptions
{
    public static readonly MessagePackSerializerOptions Default =
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithCompression(MessagePackCompression.Lz4Block);
}