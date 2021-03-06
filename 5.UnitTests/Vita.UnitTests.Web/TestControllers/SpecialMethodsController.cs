﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

using Vita.Common;
using Vita.Entities;
using Vita.Entities.Web;
using Vita.Samples.BookStore;
using Vita.Samples.BookStore.Api;
using System.Data.SqlClient;
using System.Data;
using System.Diagnostics;
using Vita.Modules.JobExecution;

namespace Vita.UnitTests.Web {

  // Service controller with special methods for testing error handling
  [ApiRoutePrefix("special")]
  public class SpecialMethodsController : SlimApiController {

    // Throws null ref exception
    [ApiGet, ApiRoute("nullref")]
    public string ThrowNullRef() {
      //Before we throw, let's write smth to local log; 
      Context.LocalLog.Info("Throwing NullReference exception...");
      string foo = null;
      if(foo.Length > 10)
        foo = "bar";
      return foo; 
    }

    // Illustrates/tests returning custom HTTP status code.
    // In real apps, NotFound should be left to situations when endpoint is not found (bad URL, page or api method does not exist).
    [ApiGet, ApiRoute("books/{id}")]
    public Book GetBook(Guid id) {
      var session = Context.OpenSystemSession();
      var bk = session.GetEntity<IBook>(id);
      if(bk == null) {
        Context.WebContext.OutgoingResponseStatus = HttpStatusCode.NotFound;
        return null;
      }
      return bk.ToModel(details: true);
    }


    // this method is used for testing handling error of bad parameter types
    [ApiGet, ApiRoute("foo/{p1}/{p2}")]
    public string Foo(int p1, Guid p2) {
      // read parameters (from part of URL and query)
      return "Foo:" + p1 + "," + p2;
    }

    public class BarParams {
      public string P1 { get; set; }
      public int P2 { get; set; }
    }

    //Test complex object with FromUrl attribute - typical use is Search queries
    // We also test that OperationContext parameter is injected automatically
    [ApiGet, ApiRoute("bar")]
    public string Bar([FromUrl] BarParams prms, OperationContext context) {
      Util.Check(context != null, "OperationContext not inserted.");
      return prms.P1 + prms.P2;
    }

    // method requires login
    [ApiGet, ApiRoute("bars"), LoggedInOnly]
    public string Bars(string q) {
      return "bars:" + q;
    }

    [ApiPost, ApiRoute("sessionvalue"), LoggedInOnly]
    public void SetSessionValue(string name, string value) {
      Context.UserSession.SetValue(name, value); 
    }
    [ApiGet, ApiRoute("sessionvalue"), LoggedInOnly]
    public string GetSessionValue(string name) {
      return Context.UserSession.GetValue<string>(name);
    }


    //See comments in TestDbConnectionHandling for details
    static string _connectionCloseReport;
    [ApiGet, ApiRoute("connectiontest")]
    public string TestConnectionHandling() {
      // connection mode should be set in WebCallContextHandlerSettings, and it is reuse by default
      if(Context.DbConnectionMode != DbConnectionReuseMode.KeepOpen)
        return "Error: Connection mode is not KeepOpen. Mode: " + Context.DbConnectionMode;
      _connectionCloseReport = "(empty)"; 
      var session = Context.OpenSession();
      session.EnableCache(false);
      var bk = session.EntitySet<IBook>().OrderBy(b => b.Title).First(); 
      //this should creaet connection and attach to session
      var entSession = (Vita.Entities.Runtime.EntitySession) session;
      var currConn = entSession.CurrentConnection;
      if(currConn == null)
        return "Connection was not attached to entity session.";
      var sqlConn = (SqlConnection)currConn.DbConnection;
      if(sqlConn.State != ConnectionState.Open)
        return "Connection was not kept open.";
      sqlConn.StateChange += Conn_StateChange;
      return "OK";
    }

    void Conn_StateChange(object sender, StateChangeEventArgs e) {
      bool isClosed = e.CurrentState == ConnectionState.Closed;
      //Check that it is being closed by proper calls, not by garbage collector cleaning up
      var callStack = Environment.StackTrace;
      bool calledConnClose = callStack.Contains("Vita.Data.DataConnection.Close()");
      bool calledPostProcessResponse = callStack.Contains("Vita.Web.WebCallContextHandler.PostProcessResponse(");
      _connectionCloseReport = string.Format("{0},{1},{2}", isClosed, calledConnClose, calledPostProcessResponse);
    }

    [ApiGet, ApiRoute("connectiontestreport")]
    public string GetConnectionnTestReport() {
      return _connectionCloseReport;
    }

    // Testing DateTime values in URL. It turns out by default WebApi uses 'model binding' for URL parameters, which 
    // results in datetime in URL (sent as UTC) to be converted to local datetime (local for server). 
    // This is inconsistent with DateTime values in body (json) - by default NewtonSoft deserializer treats them as UTC values
    // VITA provides a fix - it automatcally detects local datetimes and converts them to UTC
    // The test sends the datetime parameter to server and receives the ToString() representation; then it compares it to original. 
    // With VITA's fix, they should be identical
    [ApiGet, ApiRoute("datetostring")]
    public string GetUrlDateToString(DateTime dt) {
      return dt.ToString("u");
    }

    // this is a variation, when datetime is nullable in URL - typical case for search queries
    public class DateBox {
      public DateTime? Date { get; set; }
    }
    [ApiGet, ApiRoute("datetostring2")]
    public string GetUrlDateToString([FromUrl] DateBox dateBox) {
      var dt = dateBox.Date; 
      return dt == null ? string.Empty : dt.Value.ToString("u");
    }

    // This method tests that when task is delayed, the thread is actually 'rolled-up' correctly 
    // and SendAsync method already returned the task (which is hanging in delay)
    // This test ensures that async execution is handled correctly throughout SlimApi  
    [ApiGet, ApiRoute("getdateasync")]
    public async Task<string> GetDateAsync() {
      var webCtx = Context.WebContext;
      Util.Check(DiagnosticsHttpHandler.CallStatus == "Started", "Expected Started call status");
      var dt = this.Context.App.TimeService.UtcNow;
      var dtStr = dt.ToString("u");
      await Task.Delay(2000);
      //We now check that thread had been 'rolled up' when we hit Delay, and DiagnosticsHttpHandler.SendAsync already returned
      // We check it postfactum, when delay is over, the following line starts executing, and at this time
      // the variable in the diagnostics (top) handler had been already set. 
      Util.Check(DiagnosticsHttpHandler.CallStatus == "Returned", "Expected Started call status");
      return await Task.FromResult(dtStr);
    }

    //Testing the bug fix - calling method with redirect was failing in attempt to deserialize response
    [ApiGet, ApiRoute("redirect")]
    public void RedirectToSearch() {
      var webCtx = Context.WebContext;
      webCtx.OutgoingResponseStatus = System.Net.HttpStatusCode.Redirect;
      webCtx.OutgoingHeaders.Add("Location", "http://www.google.com");
    }

    #region test methods - sync/async bridge; manually in browser only
    // these methods test/validate sync/async bridge utitilies (trouble with deadlocks)
    // it is important that these methods are run from inside IIS host (full ASP.NET host)
    // so we call these manually when running SampleBooksUI sample web app - by typing 
    // URL in browser
    /// <summary>Internal testing only. Test method to play with thread deadlocks.</summary>
    /// <returns>Does not return, hangs forever - thread deadlock.</returns>
    [ApiGet, ApiRoute("deadlock")]
    public async Task<string> TestAsyncWithDeadlock() {
      await Task.Delay(50);
      //this should deadlock
      var result = DoDelaySyncIncorrect();
      return result;
    }

    /// <summary>Internal testing only: test sync/async bridge</summary>
    /// <returns>Current time</returns>
    [ApiGet, ApiRoute("nodeadlock")]
    public async Task<string> TestAsyncNoDeadlock() {
      await Task.Delay(50);
      return DoDelaySyncCorrect();
    }

    private string DoDelaySyncCorrect() {
      AsyncHelper.RunSync(() => DoDelayAsync()); //this is our helper method, we test it here under IIS
      return AsPlainText("Time: " + DateTime.Now.ToString("hh:MM:ss"));
    }

    // something made up, what might work in console but not under IIS (SynchronizationContext is special!)
    private string DoDelaySyncIncorrect() {
      var task = DoDelayAsync();
      var aw = task.GetAwaiter();
      while(!aw.IsCompleted)
        System.Threading.Thread.Yield();
      return AsPlainText("OK");
    }

    private async Task DoDelayAsync() {
      await Task.Delay(1000);
    }

    private string AsPlainText(string value) {
      Context.WebContext.OutgoingResponseContent = value;
      return value;
    }
    #endregion

  }//class
}
