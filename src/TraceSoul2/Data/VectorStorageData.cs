using SQLite;

namespace TraceSoul2.Data
{
    [Table("vector_embeddings")]
    public sealed class VectorEmbeddingRecord
    {
        // nodeId/definition、nodeId/positive/0 等稳定键。
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string NodeId { get; set; }

        public string TextRole { get; set; }
        public int ExampleIndex { get; set; }

        [Indexed]
        public string ModelId { get; set; }

        public string ContentHash { get; set; }
        public int Dimensions { get; set; }
        public byte[] Values { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("vector_ontology_nodes")]
    public sealed class VectorOntologyNodeRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string Level { get; set; }
        public string Label { get; set; }
        public string Definition { get; set; }
        [Indexed]
        public string DimensionKey { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("vector_ontology_domains")]
    public sealed class VectorOntologyDomainRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string NodeId { get; set; }
        [Indexed]
        public string Domain { get; set; }
    }

    [Table("vector_ontology_parents")]
    public sealed class VectorOntologyParentRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string NodeId { get; set; }
        [Indexed]
        public string ParentId { get; set; }
    }

    [Table("vector_ontology_examples")]
    public sealed class VectorOntologyExampleRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string NodeId { get; set; }
        [Indexed]
        public string Role { get; set; }
        public int ExampleIndex { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// 第四层条目的语义向量：对条目的一句话总结编码，供记忆神经在定位范围内
    /// 做余弦 top-k 拼装最相近的细节。模型变化时按 ModelId 区分。
    /// </summary>
    [Table("event_entry_vectors")]
    public sealed class VectorEntryEmbeddingRecord
    {
        [PrimaryKey]
        public string EntryId { get; set; }

        [Indexed]
        public string ModelId { get; set; }

        public string ContentHash { get; set; }
        public int Dimensions { get; set; }
        public byte[] Values { get; set; }
        public long UpdatedUnixMs { get; set; }
    }
}
