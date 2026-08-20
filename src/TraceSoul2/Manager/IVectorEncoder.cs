using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    public interface IVectorEncoder
    {
        string ModelId { get; }
        int Dimensions { get; }
        float[] Encode(string text, VectorTextPurpose purpose);
    }

    public interface IVectorCacheStore
    {
        bool TryGet(string id, string modelId, string contentHash, out float[] vector);
        void Put(string id, string nodeId, string textRole, int exampleIndex, string modelId, string contentHash, float[] vector);
    }
}
