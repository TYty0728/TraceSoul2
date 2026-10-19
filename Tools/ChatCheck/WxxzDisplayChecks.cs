using System;
using System.Threading.Tasks;
using TraceSoul2.Host;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Logic;

internal static partial class Program
{
    private static async Task CheckWxxzDisplayAsync()
    {
        Require(WxxzPlatform.BridgeAddress(null,null).url.AbsoluteUri=="http://127.0.0.1:5089/","本机桥默认地址兼容");
        var bridgeKey=new string('k',40);
        Require(WxxzPlatform.BridgeAddress("https://body.example.com/wxxz",bridgeKey).url.AbsoluteUri=="https://body.example.com/wxxz/","Docker可显式配置远端私有桥前缀");
        foreach(var bad in new[]{"http://body.example.com/","http://host.docker.internal:5089/","file:///tmp/bridge","https://" + "user:password@" + "example.com/","https://body.example.com/?key=value","https://body.example.com/#fragment"})
        {try{WxxzPlatform.BridgeAddress(bad,bridgeKey);throw new Exception("unsafe bridge accepted");}catch(InvalidOperationException){}}
        try{WxxzPlatform.BridgeAddress("https://body.example.com/",null);throw new Exception("missing bridge credential accepted");}catch(InvalidOperationException){}
        var now=DateTimeOffset.UtcNow;
        var attention=new WxxzAttention(null,()=>"测试同伴",()=>now);
        var display=new WxxzDisplay();
        WxxzDisplay.Snapshot Read(WxxzVoiceMonitor.Snapshot monitor=null,bool wake=true)=>display.Read("测试同伴",attention.State(),monitor,wake);
        Require(Read().state=="offline","未连接不能伪装发呆或倾听");
        display.Reset("session");Require(Read().state=="paused","连接不等于已开始采音");
        display.StartCall("session","call");attention.Start();
        display.Capture("session","call","capture",true);
        Require(Read().state=="standby"&&Read().animate,"名字监听在未唤醒时显示发呆，可间歇蹦跶");
        attention.Complete();
        Require(Read().state=="listening"&&!Read().animate&&Read().focusRemainingMs==30000,"唤醒后倾听，30秒租期来自后台，停止跳动");
        now=now.AddSeconds(31);
        Require(Read().state=="standby"&&Read().focusRemainingMs==0,"没有新指令，采音期间也会回发呆");
        display.Capture("session","call","capture",false);
        var turn=display.BeginTurn("session");
        var output=display.Prepare("session","call","测试回复",new string('a',64));
        Require(Read().state=="thinking"&&Read().phase=="synthesizing","准备语音时仍显示思考，不能提前说话");
        display.Playing(output);Require(Read().state=="speaking"&&Read().subtitle=="测试回复"&&!Read().animate,"真正提交播放时显示本句字幕，停止跳动");
        var stopping=new WxxzVoiceMonitor.Snapshot("monitor",true,"stopping","stopping","session","call",true,null,null,null,0,1);
        Require(Read(stopping).state=="paused","请求停止优先于尚在收尾的播放");
        display.Finished(output,true);display.CompleteTurn(turn,true);attention.Complete();
        Require(Read().state=="focused"&&Read().focusRemainingMs==30000,"播完后重新开始30秒专注");
        var second=display.Prepare("session","call","无匹配图片的本轮回复");
        Require(Read().expressionId==null,"新回复无匹配图不复用旧表情");
        display.Finished(second,false);Require(Read().state=="error","交付失败不伪造已回专注");
        display.StopCall("call");display.Reset("new-session");display.StartCall("new-session","new-call");
        Require(display.Prepare("session","call","迟到的旧回复")==null,"停止后才完成模型处理的旧回复不能作为新通话画面");
        Require(Read(stopping).state!="paused","旧会话停止状态不污染新连接");
        var newTurn=display.BeginTurn("new-session");
        display.Playing(output);display.Finished(output,false);display.CompleteTurn(turn,false);
        display.Capture("session","call","capture",false);
        Require(Read().sessionId=="new-session"&&Read().state=="thinking"&&Read().subtitle=="","旧会话或旧通话的迟到结果不覆盖新状态");
        display.CompleteTurn(newTurn,true);
        Require(Read(wake:false).state=="listening","未启用唤醒的持续通话显示倾听");
        display.Reset(null);Require(Read().state=="offline"&&Read().subtitle==""&&Read().expressionId==null,"断开清空现场文字和图片");

        using var store=new SqliteMemoryManager(":memory:");store.SavePairIdentity("测试者","测试同伴","测试者");
        var services=new TracePluginServices(store,new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var plugins=new TracePluginManager(store,services);plugins.RegisterExternal(new DialogueTracePlugin());
        var bridge=new WxxzMockBridge();using var board=new WxxzPlatform(bridge);plugins.RegisterExternal(board);
        await board.StartAsync(false,default);
        var input=board.Receive(board.SessionId,Guid.NewGuid().ToString(),"合成现场输入","测试者","display-fixture");
        Require(board.DisplayState().state=="thinking","真实Receive入口接入思考状态");
        var sawSpeaking=false;
        bridge.Observe=path=>{if(path=="/api/speak") {sawSpeaking=true;Require(board.DisplayState().state=="speaking"&&board.DisplayState().subtitle=="合成现场回复","Kernel实际输出入口携带本轮字幕");}};
        await new KernelLogic(store,new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"合成现场回复\"}"),plugins).ProcessPluginEventAsync("display-fixture",input,12);
        board.CompleteResponse();
        Require(sawSpeaking&&board.DisplayState().state=="paused","交付完成后退出说话，未开启通话不伪造倾听");
        Require(bridge.Paths.Count==3,"显示投影不增加设备请求或模型调用");
        board.Shutdown();Require(board.DisplayState().state=="offline","真实关闭入口接入断线状态");
        var privateBridge=new WxxzMockBridge();
        using var remoteBoard=new WxxzPlatform(privateBridge,bridgeUrl:"https://body.example.com/",bridgeKey:bridgeKey);
        plugins.Unregister(WxxzPlatform.Id);
        plugins.RegisterExternal(remoteBoard);
        await remoteBoard.StartAsync(false,default);
        Require(privateBridge.Authorizations.Count==1&&privateBridge.Authorizations[0]=="Bearer "+bridgeKey,"显式远端桥请求带独立Bearer，不借用模型或控制台密钥");
        Console.WriteLine("WXXZ display checks passed: actual Kernel output, audio phases, focus expiry, stop/error precedence and stale-session guards.");
    }
}
