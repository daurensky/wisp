using System.Text.Json.Nodes;

namespace Wisp.Core;

/// <summary>Apply RTDB SSE put/patch events, including relative multi-path patches and deletions.</summary>
public sealed class FirebaseJsonTree
{
    public JsonNode? Root { get; private set; }

    public void Apply(FirebaseStreamEvent update)
    {
        var path = update.Path.Trim('/');
        var value = JsonNode.Parse(update.Data.GetRawText());
        if (update.Kind == "patch" && value is JsonObject patch)
        {
            foreach (var pair in patch)
                Set(string.IsNullOrEmpty(path) ? pair.Key : path + "/" + pair.Key, pair.Value?.DeepClone());
        }
        else if (update.Kind == "put")
            Set(path, value);
    }

    private void Set(string path, JsonNode? value)
    {
        if (path.Length == 0) { Root = value; return; }
        Root ??= new JsonObject();
        var parts = path.Split('/');
        JsonNode node = Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            node[parts[i]] ??= new JsonObject();
            node = node[parts[i]]!;
        }
        if (value is null && node is JsonObject obj) obj.Remove(parts[^1]);
        else node[parts[^1]] = value;
    }
}
