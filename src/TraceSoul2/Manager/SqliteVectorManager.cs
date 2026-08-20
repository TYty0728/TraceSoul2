using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLite;
using TraceSoul2.Data;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// 当前规模下 SQLite + 内存余弦遍历足够。以后节点达到数万时，
    /// 可以只替换本类为 HNSW/Qdrant，而不改 ontology 和召回逻辑。
    /// </summary>
    public sealed class SqliteVectorManager : IVectorCacheStore, IDisposable
    {
        private readonly SQLiteConnection connection;

        public SqliteVectorManager(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", "databasePath");

            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            connection = new SQLiteConnection(databasePath);
            try
            {
                // sqlite-net 已封装了需要读取返回行的正确调用；不能用 Execute(PRAGMA journal_mode)。
                connection.EnableWriteAheadLogging();
                connection.CreateTable<VectorEmbeddingRecord>();
                connection.CreateTable<VectorOntologyNodeRecord>();
                connection.CreateTable<VectorOntologyDomainRecord>();
                connection.CreateTable<VectorOntologyParentRecord>();
                connection.CreateTable<VectorOntologyExampleRecord>();
                connection.CreateTable<VectorEntryEmbeddingRecord>();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public void UpsertOntology(IEnumerable<VectorIndexNode> nodes)
        {
            if (nodes == null) throw new ArgumentNullException("nodes");
            var materialized = new List<VectorIndexNode>(nodes);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            connection.RunInTransaction(() =>
            {
                foreach (var node in materialized)
                {
                    connection.InsertOrReplace(new VectorOntologyNodeRecord
                    {
                        Id = node.Id,
                        Level = node.Level.ToString(),
                        Label = node.Label,
                        Definition = node.Definition,
                        DimensionKey = node.DimensionKey,
                        UpdatedUnixMs = now
                    });

                    connection.Execute("DELETE FROM vector_ontology_domains WHERE NodeId = ?", node.Id);
                    connection.Execute("DELETE FROM vector_ontology_parents WHERE NodeId = ?", node.Id);
                    connection.Execute("DELETE FROM vector_ontology_examples WHERE NodeId = ?", node.Id);

                    foreach (var domain in node.ApplicableDomains)
                        connection.Insert(new VectorOntologyDomainRecord { Id = node.Id + "/" + domain, NodeId = node.Id, Domain = domain });
                    foreach (var parent in node.ParentIds)
                        connection.Insert(new VectorOntologyParentRecord { Id = node.Id + "/" + parent, NodeId = node.Id, ParentId = parent });
                    for (var i = 0; i < node.PositiveExamples.Count; i++)
                        InsertExample(node.Id, "positive", i, node.PositiveExamples[i]);
                    for (var i = 0; i < node.NegativeExamples.Count; i++)
                        InsertExample(node.Id, "negative", i, node.NegativeExamples[i]);
                }
            });
        }

        public List<VectorIndexNode> GetOntology()
        {
            var domains = connection.Table<VectorOntologyDomainRecord>().ToList().GroupBy(x => x.NodeId).ToDictionary(x => x.Key, x => x.OrderBy(y => y.Domain).Select(y => y.Domain).ToArray());
            var parents = connection.Table<VectorOntologyParentRecord>().ToList().GroupBy(x => x.NodeId).ToDictionary(x => x.Key, x => x.OrderBy(y => y.ParentId).Select(y => y.ParentId).ToArray());
            var examples = connection.Table<VectorOntologyExampleRecord>().ToList().GroupBy(x => x.NodeId).ToDictionary(x => x.Key, x => x.ToList());
            var result = new List<VectorIndexNode>();

            foreach (var record in connection.Table<VectorOntologyNodeRecord>().OrderBy(x => x.Id))
            {
                VectorNodeLevel level;
                if (!Enum.TryParse(record.Level, true, out level)) continue;
                string[] nodeDomains;
                string[] nodeParents;
                List<VectorOntologyExampleRecord> nodeExamples;
                if (!domains.TryGetValue(record.Id, out nodeDomains)) nodeDomains = new string[0];
                if (!parents.TryGetValue(record.Id, out nodeParents)) nodeParents = new string[0];
                if (!examples.TryGetValue(record.Id, out nodeExamples)) nodeExamples = new List<VectorOntologyExampleRecord>();

                result.Add(new VectorIndexNode(
                    record.Id,
                    level,
                    record.Label,
                    record.Definition,
                    record.DimensionKey,
                    nodeDomains,
                    nodeParents,
                    nodeExamples.Where(x => x.Role == "positive").OrderBy(x => x.ExampleIndex).Select(x => x.Text),
                    nodeExamples.Where(x => x.Role == "negative").OrderBy(x => x.ExampleIndex).Select(x => x.Text)));
            }
            return result;
        }

        public bool TryGet(string id, string modelId, string contentHash, out float[] vector)
        {
            var record = connection.Find<VectorEmbeddingRecord>(id);
            if (record == null || record.ModelId != modelId || record.ContentHash != contentHash)
            {
                vector = null;
                return false;
            }

            vector = VectorMathUtil.FromBytes(record.Values, record.Dimensions);
            return vector != null;
        }

        public void Put(string id, string nodeId, string textRole, int exampleIndex, string modelId, string contentHash, float[] vector)
        {
            connection.InsertOrReplace(new VectorEmbeddingRecord
            {
                Id = id,
                NodeId = nodeId,
                TextRole = textRole,
                ExampleIndex = exampleIndex,
                ModelId = modelId,
                ContentHash = contentHash,
                Dimensions = vector.Length,
                Values = VectorMathUtil.ToBytes(vector),
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public void PutEntryEmbedding(string entryId, string modelId, string contentHash, float[] vector)
        {
            connection.InsertOrReplace(new VectorEntryEmbeddingRecord
            {
                EntryId = entryId,
                ModelId = modelId,
                ContentHash = contentHash,
                Dimensions = vector.Length,
                Values = VectorMathUtil.ToBytes(vector),
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public Dictionary<string, float[]> GetEntryEmbeddings(IEnumerable<string> entryIds, string modelId)
        {
            var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
            if (entryIds == null) return result;
            var wanted = new HashSet<string>(entryIds, StringComparer.Ordinal);
            if (wanted.Count == 0) return result;
            foreach (var record in connection.Table<VectorEntryEmbeddingRecord>())
            {
                if (!wanted.Contains(record.EntryId)) continue;
                if (modelId != null && record.ModelId != modelId) continue;
                var vector = VectorMathUtil.FromBytes(record.Values, record.Dimensions);
                if (vector != null) result[record.EntryId] = vector;
            }
            return result;
        }

        public int CountEntryEmbeddings()
        {
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM event_entry_vectors");
        }

        public bool HasEntryEmbedding(string entryId, string modelId, string contentHash)
        {
            var record = connection.Find<VectorEntryEmbeddingRecord>(entryId);
            return record != null && record.ModelId == modelId && record.ContentHash == contentHash;
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        private void InsertExample(string nodeId, string role, int index, string text)
        {
            connection.Insert(new VectorOntologyExampleRecord
            {
                Id = nodeId + "/" + role + "/" + index,
                NodeId = nodeId,
                Role = role,
                ExampleIndex = index,
                Text = text
            });
        }
    }
}
