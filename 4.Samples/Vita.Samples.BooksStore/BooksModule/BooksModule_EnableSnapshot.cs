﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Vita.Entities; 

namespace Vita.Samples.BookStore {
  public partial class BooksModule {
    //Locking mechanism used for MS SQL Server requires database to have Snapshot isolation level enabled (off by default). 
    // We do it using migrations - and direct SQL connector to run special SQL. 
    public override void RegisterMigrations(Data.Upgrades.DbMigrationSet migrations) {
      if (migrations.ServerType == Data.Driver.DbServerType.MsSql) {
        migrations.AddPostUpgradeAction("1.2.0.0", "EnableSnapshot", "Enable snapshot isolation for database.", EnableSnapshotIsolation);
      }
    }

    public void EnableSnapshotIsolation(IEntitySession session) {
      var dbConn = session.GetDirectDbConnector(admin: true);
      dbConn.OpenConnection();
      // First get current DB name
      var cmdGetDb = dbConn.DbConnection.CreateCommand();
      cmdGetDb.CommandText = "SELECT DB_NAME()";
      var dbName = (string) dbConn.ExecuteDbCommand(cmdGetDb, Data.DbExecutionType.Scalar);
      var cmd = dbConn.DbConnection.CreateCommand();
      cmd.CommandText = string.Format("ALTER DATABASE {0} SET ALLOW_SNAPSHOT_ISOLATION ON;", dbName);
      dbConn.ExecuteDbCommand(cmd, Data.DbExecutionType.NonQuery);
      dbConn.CloseConnection(); 
    }
  }//class
}
