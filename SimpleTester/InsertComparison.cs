using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Attributes.Jobs;
using BTDB.KVDBLayer;
using BTDB.ODBLayer;
using System;

namespace SimpleTester
{
    [SimpleJob(targetCount: 30, warmupCount: 10)]
    public class InsertComparison
    {
        static ObjectDB CreateInMemoryDb()
        {
            var kvdb = new InMemoryKeyValueDB();
            var db = new ObjectDB();
            db.Open(kvdb, true);
            return db;
        }

        static ObjectDB CreateDb(IFileCollection fc)
        {
            var kvdb = new KeyValueDB(fc);
            var db = new ObjectDB();
            db.Open(kvdb, true);
            return db;
        }

        public class UserDb
        {
            [PrimaryKey]
            public ulong CompanyId { get; set; }

            [PrimaryKey(1)]
            public ulong Id { get; set; }

            public string Name { get; set; }
        }

        public interface IUserInsertTable
        {
            bool Insert(UserDb user);
            int RemoveById(ulong companyId);
        }

        public interface IUserUpsertTable
        {
            bool Upsert(UserDb user);
            int RemoveById(ulong companyId);

        }

        public interface IUserShallowUpsertTable
        {
            bool ShallowUpsert(UserDb user);
            int RemoveById(ulong companyId);
        }

        Random _r;
        IKeyValueDB _kvDb;
        ObjectDB _db;
        Func<IObjectDBTransaction, IUserInsertTable> _insert;
        Func<IObjectDBTransaction, IUserUpsertTable> _upsert;
        Func<IObjectDBTransaction, IUserShallowUpsertTable> _shallowUpsert;

        [GlobalSetup]
        public void Setup()
        {
            _r = new Random(42);
            _kvDb = new InMemoryKeyValueDB();
            _db = new ObjectDB();
            _db.Open(_kvDb, true);
            using (var tr = _db.StartTransaction())
            {
                _insert = tr.InitRelation<IUserInsertTable>(nameof(IUserInsertTable));
                _upsert = tr.InitRelation<IUserUpsertTable>(nameof(IUserUpsertTable));
                _shallowUpsert = tr.InitRelation<IUserShallowUpsertTable>(nameof(IUserShallowUpsertTable));
                tr.Commit();
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _db.Dispose();
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            using (var tr = _db.StartTransaction())
            {
                _insert(tr).RemoveById(1);
                _upsert(tr).RemoveById(1);
                _shallowUpsert(tr).RemoveById(1);
                tr.Commit();
            }
        }

        UserDb CreateNewUser(int i) => new UserDb
        {
            CompanyId = 1,
            Id = (ulong)_r.Next(),
            Name = i.ToString(),
        };

        const int N = 1000;

        [Benchmark]
        public void Insert()
        {
            using (var tr = _db.StartTransaction())
            {
                var table = _insert(tr);
                for (int i = 0; i < N; i++)
                    table.Insert(CreateNewUser(i));
                tr.Commit();
            }
        }

        [Benchmark]
        public void Upsert()
        {
            using (var tr = _db.StartTransaction())
            {
                var table = _upsert(tr);
                for (int i = 0; i < N; i++)
                    table.Upsert(CreateNewUser(i));
                tr.Commit();
            }
        }

        [Benchmark]
        public void ShallowUpsert()
        {
            using (var tr = _db.StartTransaction())
            {
                var table = _shallowUpsert(tr);
                for (int i = 0; i < N; i++)
                    table.ShallowUpsert(CreateNewUser(i));
                tr.Commit();
            }
        }
    }
}
