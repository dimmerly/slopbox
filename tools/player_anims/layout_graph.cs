var graph = AssetDatabase.Load<AnimationGraph>(Guid.Parse("da382ae0-b73b-40d3-a8e7-984e277a8ff6"));
var nodes = graph.Nodes;
var byId = nodes.ToDictionary(n => n.Id);
const float ColumnGap = 340f, RowGap = 36f;

float Height(GraphNodeRecord n) => 56f + 26f * (n.Inputs.Count + n.PropertyInputs.Count) + 24f * n.Properties.Count;

IEnumerable<string> Feeds(GraphNodeRecord n) => n.Inputs.Select(i => i.Node).Concat(n.PropertyInputs.Values)
    .Where(id => !string.IsNullOrEmpty(id) && byId.ContainsKey(id));

var log = new System.Text.StringBuilder();

void Layout(string owner, string sink)
{
    var scope = nodes.Where(n => n.Owner == owner).ToList();
    if (scope.Count == 0 || sink == null || !byId.ContainsKey(sink)) return;

    // Column = longest path from the sink, so every wire runs left to right.
    var depth = new Dictionary<string, int> { [sink] = 0 };
    var consumers = new Dictionary<string, List<string>>();
    var queue = new Queue<string>();
    queue.Enqueue(sink);
    int guard = 0;
    while (queue.Count > 0 && guard++ < 100000)
    {
        string id = queue.Dequeue();
        foreach (string f in Feeds(byId[id]))
        {
            if (byId[f].Owner != owner) continue;
            if (!consumers.TryGetValue(f, out var list)) consumers[f] = list = new List<string>();
            if (!list.Contains(id)) list.Add(id);
            int d = depth[id] + 1;
            if (!depth.TryGetValue(f, out int old) || d > old)
            {
                depth[f] = d;
                queue.Enqueue(f);
            }
        }
    }
    int maxDepth = depth.Values.DefaultIfEmpty(0).Max();
    foreach (var n in scope.Where(n => !depth.ContainsKey(n.Id))) depth[n.Id] = maxDepth + 1;

    // Rows: each column follows the middle of what it feeds, so wires stay short and do not cross.
    var centre = new Dictionary<string, float>();
    foreach (var column in scope.GroupBy(n => depth[n.Id]).OrderBy(g => g.Key))
    {
        var items = column.Select(n =>
        {
            float want = consumers.TryGetValue(n.Id, out var cs) && cs.Any(c => centre.ContainsKey(c))
                ? cs.Where(c => centre.ContainsKey(c)).Average(c => centre[c])
                : 0f;
            // Keep a stable order among siblings: by which input of the consumer they are.
            int slot = consumers.TryGetValue(n.Id, out var cs2) && cs2.Count > 0
                ? byId[cs2[0]].Inputs.FindIndex(i => i.Node == n.Id) : 0;
            return (n, want, slot);
        }).OrderBy(t => t.want).ThenBy(t => t.slot).ToList();

        float total = items.Sum(t => Height(t.n)) + RowGap * (items.Count - 1);
        float mean = items.Count > 0 ? items.Average(t => t.want) : 0f;
        float y = mean - total / 2f;
        foreach (var (n, _, _) in items)
        {
            float h = Height(n);
            n.EditorPosition = new Float2(-depth[n.Id] * ColumnGap, y);
            centre[n.Id] = y + h / 2f;
            y += h + RowGap;
        }
    }
    log.AppendLine($"{(owner.Length == 0 ? "graph" : owner)}: {scope.Count} nodes in {maxDepth + 1} columns");
}

// The graph itself, from its root.
Layout("", graph.RootNode);

// Every state, from its output node.
foreach (var machine in nodes.Where(n => n.States.Count > 0).ToList())
{
    foreach (var state in machine.States)
    {
        var output = nodes.FirstOrDefault(n => n.Owner == state.Id && n.Type.Contains("Output"));
        Layout(state.Id, output?.Id);
    }

    // The states as a diagram: ground on the left, the air states climbing over to land on the right.
    var at = new Dictionary<string, Float2>
    {
        ["Ground"] = new Float2(0f, 0f), ["Jump"] = new Float2(320f, -200f),
        ["Fall"] = new Float2(640f, 0f), ["Land"] = new Float2(320f, 200f),
    };
    foreach (var state in machine.States)
        if (at.TryGetValue(state.Name, out var p)) state.EditorPosition = p;
}

// Labelled boxes round the top level sections.
graph.Groups.RemoveAll(g => g.Owner.Length == 0);
var top = nodes.Where(n => n.Owner.Length == 0).ToList();
void Group(string title, IEnumerable<GraphNodeRecord> members)
{
    var list = members.ToList();
    if (list.Count == 0) return;
    float minX = list.Min(n => n.EditorPosition.X) - 30f, minY = list.Min(n => n.EditorPosition.Y) - 60f;
    float maxX = list.Max(n => n.EditorPosition.X) + 270f, maxY = list.Max(n => n.EditorPosition.Y + Height(n)) + 30f;
    graph.Groups.Add(new GraphGroupRecord { Id = Guid.NewGuid().ToString("N")[..8], Title = title,
        Position = new Float2(minX, minY), Size = new Float2(maxX - minX, maxY - minY) });
}
Group("Locomotion state machine", top.Where(n => n.States.Count > 0));
Group("Aim: chest and head look at the aim point", top.Where(n => n.Type == "motion.lookAt"
    || (n.Type == "motion.parameter" && n.Properties.TryGetValue("Name", out var v) && v.Text.StartsWith("Aim"))));
Group("Hands: IK onto the held tool's grips, then grip rotation", top.Where(n => n.Type is "motion.twoBoneIK" or "motion.copyConstraint"
    || (n.Type == "motion.parameter" && n.Properties.TryGetValue("Name", out var v) && v.Text.Contains("Hand"))));

Prowl.Editor.EditorAssetBackend.Instance.SaveAsset(graph);
return log.ToString();
