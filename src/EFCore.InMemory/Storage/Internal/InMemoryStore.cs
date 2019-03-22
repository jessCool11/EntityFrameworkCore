// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Internal;
using Microsoft.EntityFrameworkCore.InMemory.ValueGeneration.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Update;

namespace Microsoft.EntityFrameworkCore.InMemory.Storage.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class InMemoryStore : IInMemoryStore
    {
        private readonly IInMemoryTableFactory _tableFactory;
        private readonly bool _useNameMatching;

        private readonly object _lock = new object();

        private Dictionary<object, IInMemoryTable> _tables;

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public InMemoryStore([NotNull] IInMemoryTableFactory tableFactory)
            : this(tableFactory, useNameMatching: false)
        {
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public InMemoryStore(
            [NotNull] IInMemoryTableFactory tableFactory,
            bool useNameMatching)
        {
            _tableFactory = tableFactory;
            _useNameMatching = useNameMatching;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual InMemoryIntegerValueGenerator<TProperty> GetIntegerValueGenerator<TProperty>(
            IProperty property)
        {
            lock (_lock)
            {
                var entityType = property.DeclaringEntityType;
                var key = _useNameMatching ? (object)entityType.Name : entityType;

                return EnsureTable(key, entityType).GetIntegerValueGenerator<TProperty>(property);
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual bool EnsureCreated(
            StateManagerDependencies stateManagerDependencies,
            IDiagnosticsLogger<DbLoggerCategory.Update> updateLogger)
        {
            lock (_lock)
            {
                var valuesSeeded = _tables != null;
                if (valuesSeeded)
                {
                    // ReSharper disable once AssignmentIsFullyDiscarded
                    _tables = CreateTables();

                    var stateManager = new StateManager(stateManagerDependencies);
                    var entries = new List<IUpdateEntry>();
                    foreach (var entityType in stateManagerDependencies.Model.GetEntityTypes())
                    {
                        foreach (var targetSeed in entityType.GetData())
                        {
                            var entry = stateManager.CreateEntry(targetSeed, entityType);
                            entry.SetEntityState(EntityState.Added);
                            entries.Add(entry);
                        }
                    }

                    ExecuteTransaction(entries, updateLogger);
                }

                return valuesSeeded;
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual bool Clear()
        {
            lock (_lock)
            {
                if (_tables == null)
                {
                    return false;
                }

                _tables = CreateTables();

                return true;
            }
        }

        private static Dictionary<object, IInMemoryTable> CreateTables()
            => new Dictionary<object, IInMemoryTable>();

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual IReadOnlyList<InMemoryTableSnapshot> GetTables(IEntityType entityType)
        {
            var data = new List<InMemoryTableSnapshot>();
            lock (_lock)
            {
                if (_tables == null)
                {
                    _tables = CreateTables();
                    foreach (var et in entityType.GetDerivedTypesInclusive().Where(et => !et.IsAbstract()))
                    {
                        var key = _useNameMatching ? (object)et.Name : et;
                        if (_tables.TryGetValue(key, out var table))
                        {
                            data.Add(new InMemoryTableSnapshot(et, table.SnapshotRows()));
                        }
                    }
                }
            }

            return data;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual int ExecuteTransaction(
            IReadOnlyList<IUpdateEntry> entries,
            IDiagnosticsLogger<DbLoggerCategory.Update> updateLogger)
        {
            var rowsAffected = 0;

            lock (_lock)
            {
                // ReSharper disable once ForCanBeConvertedToForeach
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var entityType = entry.EntityType;

                    Debug.Assert(!entityType.IsAbstract());

                    var key = _useNameMatching ? (object)entityType.Name : entityType;
                    var table = EnsureTable(key, entityType);

                    if (entry.SharedIdentityEntry != null)
                    {
                        if (entry.EntityState == EntityState.Deleted)
                        {
                            continue;
                        }

                        table.Delete(entry);
                    }

                    switch (entry.EntityState)
                    {
                        case EntityState.Added:
                            table.Create(entry);
                            break;
                        case EntityState.Deleted:
                            table.Delete(entry);
                            break;
                        case EntityState.Modified:
                            table.Update(entry);
                            break;
                    }

                    rowsAffected++;
                }
            }

            updateLogger.ChangesSaved(entries, rowsAffected);

            return rowsAffected;
        }

        // Must be called from inside the lock
        private IInMemoryTable EnsureTable(object key, IEntityType entityType)
        {
            if (_tables == null)
            {
                _tables = CreateTables();
            }

            if (!_tables.TryGetValue(key, out var table))
            {
                _tables.Add(key, table = _tableFactory.Create(entityType));
            }

            return table;
        }
    }
}
