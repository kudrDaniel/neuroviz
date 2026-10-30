using MessagePack;
using NeuroViz.Core.Serialization;

namespace NeuroViz.Project;

public static class ProjectSerializer
{
    public static byte[] Serialize(ProjectDocument project)
        => MessagePackSerializer.Serialize(project, MessagePackOptions.Default);

    public static ProjectDocument Deserialize(byte[] bytes)
        => MessagePackSerializer.Deserialize<ProjectDocument>(bytes, MessagePackOptions.Default);

    public static void Save(ProjectDocument project, string path)
        => File.WriteAllBytes(path, Serialize(project));

    public static ProjectDocument Load(string path)
        => Deserialize(File.ReadAllBytes(path));
}