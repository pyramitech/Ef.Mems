namespace Ef.Mems
{
    /// <summary>
    /// Thrown by generated Update paths when the entity being updated is not
    /// present in the cache (deleted, never created, or a wrong/default key).
    /// </summary>
    public class EntityNotCachedException : InvalidOperationException
    {
        public EntityNotCachedException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown at commit when the same entity (by its index key) is staged
    /// more than once in a single transaction - an entity may appear at most
    /// once per transaction, in exactly one operation.
    /// </summary>
    public class DuplicateStagedEntityException : InvalidOperationException
    {
        public DuplicateStagedEntityException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when a sequential index value is far beyond the cached row
    /// count. UseSequentialIndex backs the cache with an array sized by the
    /// largest key, so it assumes dense sequential keys; sparse keys would
    /// silently allocate enormous arrays. Disable via
    /// UseSafety(sequentialIndexGuard: false) if the gap is intentional.
    /// </summary>
    public class SparseSequentialIndexException : InvalidOperationException
    {
        public SparseSequentialIndexException(string message) : base(message) { }
    }
}
