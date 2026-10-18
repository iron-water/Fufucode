using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.Views;

// M-owned behavioral review. It must not be edited to hide worker regressions.
internal static class BrowserReview
{
 private static int _checks, _failures;
 public static void Check(bool good,string label){_checks++;if(!good)_failures++;Console.WriteLine((good?"PASS ":"FAIL ")+label);}
 public static void Case(string label,Action test){try{test();}catch(Exception error){Check(false,label+": "+error.GetType().Name+" "+error.Message);}}
 public static int Finish(){Console.WriteLine($"Review checks: {_checks}; failures: {_failures}");return _failures==0?0:1;}
 [STAThread]
 public static int Main(string[] args)
 {
  if(args.Contains("--native"))return AppBuilder.Configure<NativeReview>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
  AppBuilder.Configure<ReviewApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=true}).SetupWithoutStarting();
  BrowserTabState Tab(string id,string draft="draft")=>new(id,draft,null,null,id);
  void RestoreCase(string label,BrowserTabState[] tabs,string? active=null)=>Case(label,()=>{using var view=new BrowserView();view.RestoreState(new BrowserPageState(Version:2,ActiveTabId:active,Tabs:tabs));var valid=tabs.Where(t=>t is not null&&!string.IsNullOrWhiteSpace(t.Id)).GroupBy(t=>t.Id).Select(g=>g.First());Check(view.ViewModel.Tabs.Count>0&&view.ViewModel.Tabs.Select(t=>t.Id).Distinct().Count()==view.ViewModel.Tabs.Count&&valid.All(saved=>view.ViewModel.Tabs.Any(t=>t.Id==saved.Id&&t.AddressText==saved.AddressDraft)),label+" and preserve valid entries");});
  RestoreCase("Duplicate persisted tab IDs restore without crashing",[Tab("same"),Tab("same")],"same");
  RestoreCase("Null persisted tab entry restores without crashing",[null!,Tab("valid")],"valid");
  RestoreCase("Normalized blank ID cannot collide with a later saved ID",[Tab(""),Tab("tab-1")]);
  RestoreCase("Empty v2 tab list leaves one usable tab",[]);
  Case("Generated restore ID must not steal a saved identity",()=>{using var v=new BrowserView();v.RestoreState(new BrowserPageState(Version:2,ActiveTabId:"tab-restored-1",Tabs:[Tab("","blank-draft"),Tab("tab-restored-1","selected-real-draft")]));Check(v.ViewModel.ActiveTab.AddressText=="selected-real-draft"&&v.ViewModel.ActiveTab.Id=="tab-restored-1","Generated IDs cannot steal the selected saved tab identity");});
  Case("Unknown active ID",()=>{using var v=new BrowserView();v.RestoreState(new BrowserPageState(Version:2,ActiveTabId:"absent",Tabs:[Tab("a"),Tab("b")]));Check(v.ViewModel.Tabs.Contains(v.ViewModel.ActiveTab),"Unknown active ID falls back to a restored tab");});
  Case("Restore same snapshot twice",()=>{using var v=new BrowserView();var s=new BrowserPageState(Version:2,ActiveTabId:"b",Tabs:[Tab("a","draft-a"),Tab("b","draft-b")]);v.RestoreState(s);v.RestoreState(s);Check(v.ViewModel.Tabs.Count==2&&v.HostCountForTesting==2&&v.ViewModel.AddressText=="draft-b","Repeated restore neither duplicates hosts nor loses the selected draft");});
  Case("Per-tab user state",()=>{using var v=new BrowserView();var a=v.ViewModel.ActiveTab;v.ViewModel.ApplyZoom(170);v.ViewModel.IsFindOpen=true;v.ViewModel.FindQuery="a-query";var b=v.ViewModel.CreateTab();v.ViewModel.ApplyZoom(80);v.ViewModel.FindQuery="b-query";v.ViewModel.SelectTab(a);Check(v.ViewModel.ZoomPercent==170&&v.ViewModel.FindQuery=="a-query"&&v.ViewModel.IsFindOpen,"Switching tabs preserves each tab's zoom and query");v.ViewModel.CloseTab(b);Check(v.ViewModel.ActiveTab==a&&v.ViewModel.ZoomPercent==170&&v.HostCountForTesting==1,"Closing another tab preserves the surviving state and host");});
  Case("Close last tab",()=>{using var v=new BrowserView();var old=v.HostForTesting;v.ViewModel.CloseTab(v.ViewModel.ActiveTab);Check(v.ViewModel.Tabs.Count==1&&v.ViewModel.ActiveTab.NavigationAddress is null&&old.IsDisposedForTesting,"Closing the last tab releases it and leaves a clear empty tab");});
  Case("Stopped find state",()=>{using var v=new BrowserView();v.ViewModel.IsFindOpen=true;v.ViewModel.FindQuery="query";v.ViewModel.ActiveTab.FindStatus="1/2";v.ViewModel.CloseFindCommand.Execute(null);Check(!v.ViewModel.IsFindOpen&&v.ViewModel.FindStatus=="","Close-find clears prompt state synchronously");});
  Case("Copy-address semantics",()=>{using var v=new BrowserView();string? copied=null;v.ViewModel.AddressCopyRequested+=(_,a)=>copied=a;v.ViewModel.ApplyNavigationState(false,false,false,"https://actual.example/");v.ViewModel.AddressText="unsubmitted.example";v.ViewModel.CopyAddressCommand.Execute(null);Check(copied=="https://actual.example/","Copy uses the real page address rather than the unsubmitted draft");});
  Case("Inactive tab events",()=>{using var v=new BrowserView();var a=v.ViewModel.ActiveTab;var b=v.ViewModel.CreateTab();b.AddressText="b-draft";v.ViewModel.ApplyNavigationStateTo(a,true,false,false,"https://a.example/");v.ViewModel.ApplyDocumentTitleTo(a,"A");Check(v.ViewModel.ActiveTab==b&&v.ViewModel.AddressText=="b-draft"&&v.ViewModel.DocumentTitle!="A","Inactive navigation/title events cannot overwrite the active tab");});
  Case("Bounded zoom",()=>{using var v=new BrowserView();v.ViewModel.ApplyZoom(int.MaxValue);Check(v.ViewModel.ZoomPercent==200,"Zoom clamps at 200 percent");v.ViewModel.ApplyZoom(int.MinValue);Check(v.ViewModel.ZoomPercent==50,"Zoom clamps at 50 percent");});
  return Finish();
 }
}
internal class ReviewApp:Application
{
 public override void Initialize()=>Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
}
internal sealed class NativeReview:ReviewApp
{
 private static async Task<bool> Until(Func<bool> condition,int milliseconds=8000)
 {var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(DateTime.UtcNow<end){if(condition())return true;await Task.Delay(80);}return condition();}
 private static async Task<string?> Title(BrowserViewHost host)=>await host.ExecuteScriptForTestingAsync("document.title");
 public override void OnFrameworkInitializationCompleted()
 {
  if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
  {
   var root=Path.Combine(Path.GetTempPath(),"fufucode-m-browser-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   typeof(Fufucode.App.Program).GetProperty("SelfTestMode",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,true);
   Fufucode.App.Program.SelfTestDataRoot=root;
   var first=Path.Combine(root,"first.html");File.WriteAllText(first,"<!doctype html><title>M_PENDING_OLD</title><p>marker marker</p>");
   var last=Path.Combine(root,"last.html");File.WriteAllText(last,"<!doctype html><title>M_PENDING_URL_LAST</title><p>marker marker</p>");
   var view=new BrowserView();var host=view.HostForTesting;
   host.Navigate(new Uri(first).AbsoluteUri);host.NavigateToDocument("<!doctype html><title>M_PENDING_LAST</title><p>marker marker</p>");
   view.ViewModel.ApplyZoom(175);
   var window=new Window{Title="M owned browser SDK review",Width=620,Height=480,Content=view};desktop.MainWindow=window;
   window.Opened+=(_,_)=>Dispatcher.UIThread.Post(async()=>
   {
    try
    {
     await Until(()=>host.IsReady&&view.ViewModel.DocumentTitle.StartsWith("M_PENDING_"),35000);
     var initialTitle=await Title(host);
     BrowserReview.Check(initialTitle=="\"M_PENDING_LAST\"","Last document request wins before controller initialization (actual="+initialTitle+")");
     BrowserReview.Check(host.IsReady&&Math.Abs(host.ZoomFactor-1.75)<0.001,$"Pre-initialization zoom reaches real controller (UI={view.ViewModel.ZoomPercent},native={host.ZoomFactor})");
     BrowserReview.Check(host.IsReady,"SDK runtime prerequisite is available");
     if(!host.IsReady)return;
     var oldTitle=await Title(host);var oldAddress=view.ViewModel.ActiveTab.NavigationAddress;
     view.PreviewFile(Path.Combine(root,"missing.txt"));await Task.Delay(120);
     BrowserReview.Check(await Title(host)==oldTitle&&view.ViewModel.ActiveTab.NavigationAddress==oldAddress,"Missing local file preserves the current DOM and real address");
     view.ViewModel.AddressText="javascript:document.title='BAD'";view.ViewModel.NavigateCommand.Execute(null);await Task.Delay(120);
     BrowserReview.Check(await Title(host)==oldTitle,"Rejected javascript address cannot replace the real page");
     view.ViewModel.ApplyZoom(175);BrowserReview.Check(Math.Abs(host.ZoomFactor-1.75)<0.001,"Zoom after initialization really reaches the controller");
     view.ViewModel.IsFindOpen=true;view.ViewModel.FindQuery="marker";view.ViewModel.FindNextCommand.Execute(null);
     await Until(()=>host.FindStatus?.MatchCount==2);await Task.Delay(500);
     BrowserReview.Check(host.FindStatus?.MatchCount==2,"First find starts a real two-match SDK session");
     // Start another observation and close immediately, while its delayed callback is pending.
     view.ViewModel.FindNextCommand.Execute(null);
     view.ViewModel.CloseFindCommand.Execute(null);await Task.Delay(600);
     BrowserReview.Check(!view.ViewModel.IsFindOpen,"Close-find really hides the find bar");
     await Task.Delay(3200);
     BrowserReview.Check(view.ViewModel.FindStatus=="","Cancelled find remains cleared after all pending polling finishes (actual="+view.ViewModel.FindStatus+")");
     host.NavigateToDocument("<!doctype html><title>M_FIND_REOPEN</title><p>marker</p>");
     await Until(()=>view.ViewModel.DocumentTitle=="M_FIND_REOPEN");
     view.ViewModel.IsFindOpen=true;view.ViewModel.FindNextCommand.Execute(null);
     await Until(()=>host.FindStatus?.MatchCount==1,4200);
     BrowserReview.Check(host.FindStatus?.MatchCount==1,"Reopening the same query finds the NEW document's one match (native="+host.FindStatus+")");
     view.ViewModel.CloseFindCommand.Execute(null);
     var a=view.ViewModel.ActiveTab;var b=view.ViewModel.CreateTab();var hostB=view.HostForTesting;
     hostB.NavigateToDocument("<!doctype html><title>M_DOC_FIRST</title><p>doc first</p>");hostB.Navigate(new Uri(last).AbsoluteUri);
     view.ViewModel.ApplyZoom(140);
     await Until(()=>hostB.IsReady&&b.DocumentTitle=="M_PENDING_URL_LAST");
     BrowserReview.Check(await Title(hostB)=="\"M_PENDING_URL_LAST\"","Last URL request also wins before initialization");
     BrowserReview.Check(Math.Abs(hostB.ZoomFactor-1.4)<0.001,$"New-tab early zoom reaches its own controller (UI={b.ZoomPercent},native={hostB.ZoomFactor})");
     view.ViewModel.ApplyZoom(140);view.ViewModel.SelectTab(a);
     BrowserReview.Check(Math.Abs(host.ZoomFactor-1.75)<0.001&&Math.Abs(hostB.ZoomFactor-1.4)<0.001,"Two ready controllers preserve independent zoom when switching tabs");
     view.ViewModel.CloseTab(b);
     BrowserReview.Check(hostB.IsDisposedForTesting&&!hostB.HasControllerForTesting&&host.IsReady,"Closing B releases only B's real controller");
     // Navigation via browser history must invalidate the same-query find session too.
     var triple=Path.Combine(root,"triple.html");File.WriteAllText(triple,"<!doctype html><title>M_HISTORY_THREE</title><p>marker marker marker</p>");
     view.ViewModel.AddressText=last;view.ViewModel.NavigateCommand.Execute(null);
     await Until(()=>view.ViewModel.DocumentTitle=="M_PENDING_URL_LAST");
     view.ViewModel.AddressText=triple;view.ViewModel.NavigateCommand.Execute(null);
     await Until(()=>view.ViewModel.DocumentTitle=="M_HISTORY_THREE");
     view.ViewModel.IsFindOpen=true;view.ViewModel.FindQuery="marker";view.ViewModel.FindNextCommand.Execute(null);
     await Until(()=>host.FindStatus?.MatchCount==3);
     BrowserReview.Check(host.FindStatus?.MatchCount==3,"History scenario starts a real THREE-match find session");
     view.ViewModel.BackCommand.Execute(null);await Until(()=>view.ViewModel.DocumentTitle=="M_PENDING_URL_LAST");
     view.ViewModel.FindNextCommand.Execute(null);await Until(()=>host.FindStatus?.MatchCount==2,4200);
     BrowserReview.Check(host.FindStatus?.MatchCount==2,"Back navigation restarts same-query find against its TWO-match document (actual="+host.FindStatus+")");
     view.ViewModel.CloseFindCommand.Execute(null);
     var stoppedTab=view.ViewModel.CreateTab();var stoppedHost=view.HostForTesting;
     view.ViewModel.AddressText=first;view.ViewModel.NavigateCommand.Execute(null);
     var stopBeforeReady=!stoppedHost.IsReady&&view.ViewModel.CanStop;
     view.ViewModel.StopCommand.Execute(null);
     BrowserReview.Check(stopBeforeReady,"Stop scenario actually accepts Stop while initialization is pending");
     await Until(()=>stoppedHost.IsReady);await Task.Delay(800);
     var stoppedTitle=await Title(stoppedHost);
     BrowserReview.Check(stoppedTitle=="\"\""&&!stoppedTab.IsLoading,"Stop before initialization prevents the cancelled page from loading (actual="+stoppedTitle+")");
     view.ViewModel.CloseTab(stoppedTab);view.ViewModel.SelectTab(a);
     var retryTab=view.ViewModel.CreateTab();var retryHost=view.HostForTesting;
     view.ViewModel.AddressText=first;view.ViewModel.NavigateCommand.Execute(null);
     var retryBeforeReady=!retryHost.IsReady;
     view.ViewModel.StopCommand.Execute(null);
     BrowserReview.Check(!retryTab.IsLoading,"Stop immediately converges loading state before controller is ready");
     view.ViewModel.AddressText=last;view.ViewModel.NavigateCommand.Execute(null);
     await Until(()=>retryHost.IsReady);await Task.Delay(700);
     BrowserReview.Check(retryBeforeReady&&await Title(retryHost)=="\"M_PENDING_URL_LAST\"","A NEW navigation after pending Stop is not silently cancelled (actual="+await Title(retryHost)+")");
     view.ViewModel.CloseTab(retryTab);view.ViewModel.SelectTab(a);
     var race=new BrowserView();var raceHost=race.HostForTesting;raceHost.Navigate(new Uri(last).AbsoluteUri);
     var oldWindow=new Window{Width=450,Height=350,Content=race};oldWindow.Show(window);oldWindow.UpdateLayout();
     var inFlight=raceHost.IsInitializingForTesting&&!raceHost.IsReady;
     oldWindow.Content=null;var newWindow=new Window{Width=460,Height=360,Content=race};newWindow.Show(window);oldWindow.Close();
     await Until(()=>raceHost.IsReady,8000);
     BrowserReview.Check(inFlight,"Cross-window test actually starts with controller creation pending");
     BrowserReview.Check(raceHost.IsReady&&raceHost.ParentWindowForTesting==newWindow.TryGetPlatformHandle()?.Handle,"Pending initialization survives destruction of its former parent window");
     race.Dispose();newWindow.Close();
     var closing=new BrowserView();var closingHost=closing.HostForTesting;var shortWindow=new Window{Width=410,Height=330,Content=closing};shortWindow.Show(window);shortWindow.UpdateLayout();
     closing.Dispose();shortWindow.Close();await Task.Delay(900);
     BrowserReview.Check(closingHost.IsDisposedForTesting&&!closingHost.HasControllerForTesting,"Dispose during creation leaves no live controller");
     Console.WriteLine("SDK isolated root: "+root);
    }
    catch(Exception error){BrowserReview.Check(false,"SDK review exception: "+error);}
    finally{view.Dispose();window.Close();desktop.Shutdown(BrowserReview.Finish());}
   });
  }
  base.OnFrameworkInitializationCompleted();
 }
}
