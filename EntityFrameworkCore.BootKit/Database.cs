﻿using Dapper;
using EntityFrameworkCore.BootKit.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EntityFrameworkCore.BootKit;

public class Database
{
    public List<DatabaseBind> DbContextBinds;

    public Database()
    {
        DbContextBinds = new List<DatabaseBind>();
    }

    public DatabaseBind GetBinding(Type tableInterface)
    {
        var binding = DbContextBinds.FirstOrDefault(x => (x.TableInterface != null && x.TableInterface.Equals(tableInterface)) || 
            (x.Entities != null && x.Entities.Select(e => e.Name).Contains(tableInterface.Name)));

        if (binding == null)
        {
            throw new Exception($"Can't find binding for interface {tableInterface.ToString()}");
        }

        return binding;
    }

    public DatabaseBind GetBinding(string tableName)
    {
        var binding = DbContextBinds.FirstOrDefault(x => x.Entities != null && x.Entities.Select(entity => entity.Name.ToLower()).Contains(tableName.ToLower()));

        if (binding == null)
        {
            throw new Exception($"Can't find binding for table {tableName}");
        }

        return binding;
    }

    private List<Type> GetAllEntityTypes(DatabaseBind bind)
    {
        var assemblies = (string[])AppDomain.CurrentDomain.GetData("Assemblies");
        return Utility.GetClassesWithInterface(bind.TableInterface, assemblies);
    }

    public void BindDbContext(DatabaseBind bind)
    {
        bind.Entities = GetAllEntityTypes(bind).ToList();

        DbContextBinds.Add(bind);
    }

    public void BindDbContext<TTableInterface, TDbContextType>(DatabaseBind bind)
    {
        bind.TableInterface = typeof(TTableInterface);
        bind.DbContextType = typeof(TDbContextType);

        bind.Entities = GetAllEntityTypes(bind).ToList();

        if (bind.SlaveConnections == null)
            bind.SlaveConnections = new List<DbConnection>();

        if (bind.SlaveConnections.Count == 0)
            bind.SlaveConnections.Add(bind.MasterConnection);

        // random
        bind.SlaveId = new Random().Next(bind.SlaveConnections.Count);

        DbContextBinds.Add(bind);

        if (bind.CreateDbIfNotExist)
            GetMaster(bind.TableInterface).Database.EnsureCreated();
    }

    public DataContext GetMaster(Type tableInterface)
    {
        var binding = GetBinding(tableInterface);

        if (binding.DbContextMaster == null)
        {
            DbContextOptions options = new DbContextOptions<DataContext>();
            DataContext dbContext = Activator.CreateInstance(binding.DbContextType, options, binding.ServiceProvider) as DataContext;
            dbContext.ConnectionString = binding.MasterConnection.ConnectionString;
            dbContext.EntityTypes = binding.Entities;
            binding.DbContextMaster = dbContext;
        }

        return binding.DbContextMaster;
    }

    public DataContext GetReader(Type tableInterface)
    {
        var binding = GetBinding(tableInterface);

        if (binding.DbContextSlaver == null)
        {
            DbContextOptions options = new DbContextOptions<DataContext>();

            DataContext dbContext = Activator.CreateInstance(binding.DbContextType, options, binding.ServiceProvider) as DataContext;
            dbContext.EntityTypes = binding.Entities;

            if (binding.SlaveConnections == null || binding.SlaveConnections.Count == 0)
                dbContext.ConnectionString = binding.MasterConnection.ConnectionString;
            else
                dbContext.ConnectionString = binding.SlaveConnection.ConnectionString;

            binding.DbContextSlaver = dbContext;
        }

        return binding.DbContextSlaver;
    }

    private string RandomConn(List<DbConnection> connetions)
    {
        int idx = new Random().Next(connetions.Count);
        return connetions[idx].ConnectionString;
    }

    /*public IMongoCollection<T> Collection<T>(string collection) where T : class
    {
        DatabaseBind binding = GetBinding(typeof(T));
        if (binding.DbContextMaster == null)
        {
            binding.DbContext = new MongoDbContext(binding.ConnectionString);
        }

        return binding.DbContextSlavers.First();
    }*/

    public Object Find(Type type, params string[] keys)
    {
        DatabaseBind binding = DbContextBinds.First(x => x.Entities.Contains(type));
        if (binding.DbContextMaster == null || binding.DbContextMaster.Database.CurrentTransaction == null)
        {
            return GetReader(type).Find(type, keys);
        }
        else
        {
            return GetMaster(type).Find(type, keys);
        }
    }

    public void Add<TTableInterface>(object entity)
    {
        var db = GetMaster(typeof(TTableInterface));
        db.Add(entity);
    }

    public void Add<TTableInterface>(IEnumerable<object> entities)
    {
        var db = GetMaster(typeof(TTableInterface));
        db.AddRange(entities);
    }

    public void Delete<TTableInterface>(object entity)
    {
        var db = GetMaster(typeof(TTableInterface));
        db.Remove(entity);
    }

    public void Delete<TTableInterface>(IEnumerable<object> entities)
    {
        var db = GetMaster(typeof(TTableInterface));
        db.RemoveRange(entities);
    }

    public DbSet<T> Table<T>() where T : class
    {
        Type entityType = typeof(T);

        DatabaseBind binding = DbContextBinds.First(x => x.Entities.Contains(entityType));
        if (binding.DbContextMaster == null || binding.DbContextMaster.Database.CurrentTransaction == null)
        {
            return GetReader(entityType).Set<T>();
        }
        else
        {
            return GetMaster(entityType).Set<T>();
        }
    }

    public int ExecuteSqlCommand<T>(string sql, params object[] parameterms)
    {
        var db = GetMaster(typeof(T)).Database;
        return db.ExecuteSqlRaw(sql, parameterms);
    }

    public Task<int> ExecuteAsync<T>(string sql, 
        IEnumerable<object> parameters = default, 
        DbExecutionOptions options = null,
        CancellationToken cancellationToken = default)
    {
        var db = GetMaster(typeof(T)).Database;
        if (options != null)
        {
            db.SetCommandTimeout(options.Timeout);
        }
        return db.ExecuteSqlRawAsync(sql, parameters ?? [], cancellationToken);
    }

    public IEnumerable<TResult> Query<TTableInterface, TResult>(string sql, object parameterms = null)
    {
        var conn = GetBinding(typeof(TTableInterface)).SlaveConnection;
        try
        {
            conn.Open();
            return conn.Query<TResult>(sql, parameterms);
        }
        finally
        {
            conn.Close();
        }
    }

    public IEnumerable<dynamic> Query<TTableInterface>(string sql, object parameterms = null)
    {
        var conn = GetBinding(typeof(TTableInterface)).SlaveConnection;
        try
        {
            conn.Open();
            return conn.Query(sql, parameterms);
        }
        finally
        {
            conn.Close();
        }
    }

    public int SaveChanges()
    {
        var bindings = DbContextBinds.Where(x => x.DbContextType != null)
            .Where(x => x.IsRelational && x.DbContextMaster != null)
            .ToList();

        if (bindings.Count() == 0)
        {
            throw new Exception($"Current transaction is not open.");
        }

        var affectedRows = 0;
        foreach (var binding in bindings)
        {
            if (binding.DbContextMaster.Database.CurrentTransaction != null)
                affectedRows += binding.DbContextMaster.SaveChanges();
        }

        return affectedRows;
    }

    public async Task<int> SaveChangesAsync()
    {
        var bindings = DbContextBinds.Where(x => x.DbContextType != null).Where(x => x.DbContextMaster != null);
        if (bindings.Count() == 0)
        {
            throw new Exception($"Current transaction is not open.");
        }
        var affectedRows = 0;
        var tasks = new List<Task<int>>();
        foreach (var binding in bindings)
        {
            if (binding.DbContextMaster.Database.CurrentTransaction != null)
                tasks.Add(binding.DbContextMaster.SaveChangesAsync());
        }
        await Task.WhenAll();
        foreach (var task in tasks)
            affectedRows += task.Result;
        return affectedRows;
    }

}
