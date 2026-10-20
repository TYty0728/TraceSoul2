using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Host;

internal static partial class Program
{
    private static async Task CheckWxxzDeviceControlAsync()
    {
        int exchanges=0,starts=0,stops=0;bool active=false,fail=false,paused=false,failStart=false;
        long sequence=0;string action="",boot=Guid.NewGuid().ToString("N"),owner=null;
        WxxzDeviceControl.Request last=null;
        using var control=new WxxzDeviceControl((request,token)=>
        {
            exchanges++;last=request;
            if(fail)throw new IOException("synthetic_transport_failure");
            owner??=request.controller;
            return Task.FromResult(new WxxzDeviceControl.Receipt(request.controller,boot,sequence,action,paused));
        },(command,token)=>
        {
            if(command=="start"){starts++;if(failStart)throw new InvalidOperationException("synthetic_start_failure");active=true;}
            else{stops++;active=false;}
            return Task.CompletedTask;
        },()=> (active,active?"listening":"paused",true));
        await control.TickAsync(CancellationToken.None);
        Require(exchanges==0&&!control.Status().enabled,"未授权不轮询开发板，不采音");
        await control.ConfigureAsync(true,CancellationToken.None);
        sequence=7;action="start";
        await control.TickAsync(CancellationToken.None);
        Require(starts==0&&control.Status().handled==7,"初次同步只建立基线，丢弃授权前按钮事件");
        sequence=8;await control.TickAsync(CancellationToken.None);
        await control.TickAsync(CancellationToken.None);
        Require(starts==1&&active&&last.ack==8,"物理开始只执行一次，重复回执不重复采音");
        sequence=9;action="stop";await control.TickAsync(CancellationToken.None);
        Require(!active&&stops>0,"物理结束进入真实停止回调");
        int previous=starts;sequence=10;action="start";paused=true;
        await control.TickAsync(CancellationToken.None);
        Require(starts==previous&&!active,"板端暂停时拒绝开始，不绕过麦克风开关");
        paused=false;failStart=true;sequence=11;
        await control.TickAsync(CancellationToken.None);await control.TickAsync(CancellationToken.None);
        Require(starts==previous+1&&!active&&control.Status().error!=null,"开始失败后不自动重试，需要新按钮事件");
        failStart=false;sequence=12;await control.TickAsync(CancellationToken.None);
        Require(active&&control.Status().error==null,"新物理事件可在修复后重新开始");
        var beforeFailure=last.controller;
        fail=true;await control.TickAsync(CancellationToken.None);
        Require(!active&&!control.Status().online,"传输失败结束通话，不自动恢复");
        fail=false;sequence=13;action="start";previous=starts;
        await control.TickAsync(CancellationToken.None);
        Require(last.controller!=beforeFailure&&starts==previous,"重连换控制代次，丢弃结果未知期间的旧开始事件");
        boot=Guid.NewGuid().ToString("N");sequence=1;await control.TickAsync(CancellationToken.None);
        Require(starts==previous,"开发板重启后重新建立基线，不重放开始");
        await control.ConfigureAsync(false,CancellationToken.None);
        int count=exchanges;await control.TickAsync(CancellationToken.None);
        Require(exchanges==count&&!control.Status().enabled&&!last.allowed,"撤销授权发送停止租约，之后不再轮询");
        Require(WxxzPlatform.SupportsRealtimeFirmware("0.3.0")&&WxxzPlatform.SupportsRealtimeFirmware("0.4.3")&&
            !WxxzPlatform.SupportsRealtimeFirmware("0.5.0")&&!WxxzPlatform.SupportsRealtimeFirmware("garbage"),
            "实时协议接受已兼容0.3/0.4版本，拒绝未知版本");
        await CheckWxxzDeviceStartConfirmationAsync();
        Console.WriteLine("WXXZ touch control checks passed: explicit authorization, dedup, stale boot/lease rejection, stop, no retry and start confirmation before capture.");
    }
    private static async Task CheckWxxzDeviceStartConfirmationAsync()
    {
        WxxzDeviceControl control=null;bool active=false,stopDuringConfirm=false,paused=false;
        long sequence=0;string action="",boot=Guid.NewGuid().ToString("N");int captures=0;
        control=new WxxzDeviceControl((request,token)=>
        {
            if(request.active&&request.ack==sequence)
            {
                if(stopDuringConfirm){sequence++;action="stop";paused=true;}
                else action="";
            }
            return Task.FromResult(new WxxzDeviceControl.Receipt(request.controller,boot,sequence,action,paused));
        },async(command,token)=>
        {
            if(command=="stop"){active=false;return;}
            await control.ConfirmStartAsync(token);
            captures++;active=true;
        },()=> (active,"paused",true));
        using(control)
        {
            await control.ConfigureAsync(true,CancellationToken.None);
            await control.TickAsync(CancellationToken.None);
            sequence=1;action="start";await control.TickAsync(CancellationToken.None);
            Require(captures==1&&active,"Host先确认板端开始代次，再允许采音/播音");
            sequence=2;action="stop";await control.TickAsync(CancellationToken.None);
            stopDuringConfirm=true;sequence=3;action="start";await control.TickAsync(CancellationToken.None);
            Require(captures==1&&!active&&control.Status().error!=null,"开始确认途中按结束，后到的开始不能开启采音");
        }
    }
}
