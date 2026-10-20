using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TraceSoul2.Host;

internal static partial class Program
{
    private static void RunRuntimePathRepairChecks()
    {
        var temp = Path.Combine(Path.GetTempPath(), "tracesoul-path-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Dictionary<string,string> Fixture(string name, out string home, out string app)
            {
                var root = Path.Combine(temp, name); home = Path.Combine(root,"Data"); app = Path.Combine(root,"App");
                Directory.CreateDirectory(home); Directory.CreateDirectory(app);
                File.WriteAllText(Path.Combine(home,"home.json"),"{\"activeSoul\":\"main\",\"custom\":{\"keep\":true}}");
                var plugins=Path.Combine(home,"plugins"); var data=Path.Combine(home,"plugins_data");
                Directory.CreateDirectory(plugins); Directory.CreateDirectory(data);
                return new Dictionary<string,string>{{"TRACESOUL2_HOME",home},{"TRACESOUL2_PLUGINS",plugins},{"TRACESOUL2_PLUGINS_DATA",data}};
            }
            void Data(string directory,string filename="config.json",string content="{\"syntheticKey\":\"unchanged\"}")
            {Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory,filename),content);}

            var env=Fixture("misplaced",out var home,out var app);
            var role=Path.Combine(app,"main"); var plugins=Path.Combine(app,"plugins"); var pluginData=Path.Combine(app,"plugins_data");
            Data(role,"tracesoul2-brainframe.sqlite3","synthetic-db"); Data(role,"tracesoul2-brainframe.sqlite3-wal","synthetic-wal");
            Data(Path.Combine(plugins,"test"),"plugin.json","{\"dll\":\"test.dll\"}"); Data(pluginData);
            File.WriteAllText(Path.Combine(pluginData,"config.json"),new JsonObject { ["reference_path"]=Path.Combine(pluginData,"image.png"), ["api_key"]="unchanged", ["prompt"]=Path.Combine(pluginData,"do-not-rewrite-prompt") }.ToJsonString());
            Directory.CreateDirectory(Path.Combine(pluginData,"empty-cache"));
            env["TRACESOUL2_DATA"]=role; env["TRACESOUL2_PLUGINS"]=plugins; env["TRACESOUL2_PLUGINS_DATA"]=pluginData;
            var oldEnv=new Dictionary<string,string>(env);
            var report=RuntimePathRepair.Check(home,app,env);
            Require(report.Moves.Count==3,"错误程序内目录全部复制迁移");
            Require(env["TRACESOUL2_DATA"]==Path.Combine(home,"souls","main"),"角色切换到持久目录");
            Require(File.ReadAllText(Path.Combine(env["TRACESOUL2_DATA"],"tracesoul2-brainframe.sqlite3-wal"))=="synthetic-wal","WAL原样随库移动");
            Require(Directory.Exists(Path.Combine(env["TRACESOUL2_PLUGINS_DATA"],"empty-cache")),"空目录也保留");
            Require(report.Moves.All(m=>Directory.Exists(m.Source)&&m.OriginalBackup==m.Source&&m.SourceFingerprint?.Length==64&&m.CopyVerifiedUtc.HasValue),"旧来源原路径保留且记录校验指纹");
            Require(File.ReadAllText(Path.Combine(role,"tracesoul2-brainframe.sqlite3"))=="synthetic-db"&&File.ReadAllText(Path.Combine(role,"tracesoul2-brainframe.sqlite3-wal"))=="synthetic-wal","旧库及WAL原路径原样保留");
            using(var originalConfig=JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginData,"config.json"))))
                Require(originalConfig.RootElement.GetProperty("reference_path").GetString()==Path.Combine(pluginData,"image.png"),"旧设置原路径和内容均不修改");
            using(var fixedConfig=JsonDocument.Parse(File.ReadAllText(Path.Combine(env["TRACESOUL2_PLUGINS_DATA"],"config.json"))))
            {
                Require(fixedConfig.RootElement.GetProperty("reference_path").GetString()==Path.Combine(home,"plugins_data","image.png"),"设置中指向已搬资料的绝对路径同步更新");
                Require(fixedConfig.RootElement.GetProperty("api_key").GetString()=="unchanged"&&fixedConfig.RootElement.GetProperty("prompt").GetString()==Path.Combine(pluginData,"do-not-rewrite-prompt"),"密钥和提示正文不改写");
            }
            Require(report.ConfigBackups.Count>=1&&report.ConfigBackups.Values.All(File.Exists),"配置改写前保留原件");
            Data(env["TRACESOUL2_PLUGINS_DATA"],"destination-new.txt","new destination runtime data");
            var replay=RuntimePathRepair.Check(home,app,oldEnv);
            Require(replay.Moves.Count==3&&oldEnv["TRACESOUL2_PLUGINS_DATA"]==Path.Combine(home,"plugins_data"),"旧启动参数重放不重复迁移");
            Require(File.Exists(Path.Combine(oldEnv["TRACESOUL2_PLUGINS_DATA"],"destination-new.txt")),"目标正常产生新资料后重启不回写原件");
            using(var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(home,"home.json"))))
                Require(config.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean(),"配置未知字段保留");
            Data(pluginData,"new-data.txt","new data at retired path");
            MustReject(()=>RuntimePathRepair.Check(home,app,env),"使用新路径启动仍检测旧目录被写入");
            MustReject(()=>RuntimePathRepair.Check(home,app,new Dictionary<string,string>{{"TRACESOUL2_HOME",home},{"TRACESOUL2_PLUGINS",plugins},{"TRACESOUL2_PLUGINS_DATA",pluginData}}),"旧路径重新产生资料时不猜测覆盖");

            env=Fixture("valid-custom",out home,out app);
            var custom=Path.Combine(temp,"custom-volume"); Data(custom);
            env["TRACESOUL2_PLUGINS_DATA"]=custom;
            report=RuntimePathRepair.Check(home,app,env);
            Require(report.Moves.Count==0&&env["TRACESOUL2_PLUGINS_DATA"]==custom,"合法外部自定义/挂载路径保持原位");

            env=Fixture("after-upgrade",out home,out app);
            pluginData=Path.Combine(app,"plugins_data"); env["TRACESOUL2_PLUGINS_DATA"]=pluginData;
            var oldApp=Path.Combine(Path.GetDirectoryName(app),".App.tracesoul2-backup-0.0.1-20261020-010203");
            Data(Path.Combine(oldApp,"plugins_data","example"));
            report=RuntimePathRepair.Check(home,app,env);
            Require(report.Moves.Count==1&&File.Exists(Path.Combine(home,"plugins_data","example","config.json")),"更新后从唯一最新应用备份找回错误放置的资料");

            env=Fixture("conflict",out home,out app);
            pluginData=Path.Combine(app,"plugins_data"); Data(pluginData,"config.json","{\"side\":1}");
            Data(Path.Combine(home,"plugins_data"),"config.json","{\"side\":2}"); env["TRACESOUL2_PLUGINS_DATA"]=pluginData;
            MustReject(()=>RuntimePathRepair.Check(home,app,env),"目标已有不同资料时不覆盖");
            Require(File.ReadAllText(Path.Combine(pluginData,"config.json")).Contains("1")&&File.ReadAllText(Path.Combine(home,"plugins_data","config.json")).Contains("2"),"冲突两侧均保留");

            env=Fixture("archive-location",out home,out app);
            var archive=Path.Combine(home,"souls","main","archives"); Data(Path.Combine(archive,"packages"),"example.tsbackup","encrypted-fixture");
            File.WriteAllText(Path.Combine(home,"archive-settings.json"),JsonSerializer.Serialize(new {directory=archive,enabled=true,keepCount=28}));
            report=RuntimePathRepair.Check(home,app,env);
            Require(report.Moves.Any(m=>m.Kind=="archives")&&File.Exists(Path.Combine(home,"archives","packages","example.tsbackup")),"错误嵌在角色目录的成品自动迁出");
            using(var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(home,"archive-settings.json"))))
                Require(config.RootElement.GetProperty("directory").GetString()=="archives"&&config.RootElement.GetProperty("enabled").GetBoolean(),"存档路径改正但定时开关保留");

            env=Fixture("home-in-app",out home,out app);
            var originalHome=Path.Combine(app,"Data"); Directory.CreateDirectory(originalHome);
            Data(originalHome,"home.json","{\"activeSoul\":\"main\"}"); Data(Path.Combine(originalHome,"souls","main"),"tracesoul2-brainframe.sqlite3","synthetic");
            Data(Path.Combine(originalHome,"plugins","test"),"plugin.json","{}"); Data(Path.Combine(originalHome,"plugins_data"));
            // Remove the unrelated empty test home: a real installed layout has a free canonical Data target.
            Directory.Delete(home,true);
            env["TRACESOUL2_HOME"]=originalHome; env["TRACESOUL2_PLUGINS"]=Path.Combine(originalHome,"plugins"); env["TRACESOUL2_PLUGINS_DATA"]=Path.Combine(originalHome,"plugins_data");
            var originalEnv=new Dictionary<string,string>(env);
            report=RuntimePathRepair.Check(originalHome,app,env);
            Require(env["TRACESOUL2_HOME"]==home&&File.Exists(Path.Combine(home,"souls","main","tracesoul2-brainframe.sqlite3")),"程序内家目录迁到程序旁Data");
            Require(File.ReadAllText(Path.Combine(originalHome,"home.json"))=="{\"activeSoul\":\"main\"}"&&File.Exists(Path.Combine(originalHome,"souls","main","tracesoul2-brainframe.sqlite3")),"旧家目录保留全部原件，不写重定向标记");
            Data(Path.Combine(home,"plugins_data"),"destination-new.txt","new runtime data after home copy");
            RuntimePathRepair.Check(originalHome,app,originalEnv);
            Require(originalEnv["TRACESOUL2_HOME"]==home,"旧家目录参数可在下次启动重定向");
            Require(RuntimePathRepair.ReadReport(home).Moves.Count==1&&originalEnv["TRACESOUL2_PLUGINS_DATA"]==Path.Combine(home,"plugins_data"),"家目录的子路径覆盖跟随别名，不重复复制或覆盖新数据");

            env=Fixture("foreign-path",out home,out app);
            Data(Path.Combine(home,"plugins_data","example"));
            env["TRACESOUL2_PLUGINS_DATA"]=OperatingSystem.IsWindows()?"/old-machine/plugins_data":"Z:\\old-machine\\plugins_data";
            RuntimePathRepair.Check(home,app,env);
            Require(env["TRACESOUL2_PLUGINS_DATA"]==Path.Combine(home,"plugins_data"),"旧机器路径自动对应唯一的本地资料");

            var newHome=Path.Combine(temp,"explicit-new-instance");
            env=new Dictionary<string,string>{{"TRACESOUL2_HOME",newHome}};
            RuntimePathRepair.Check(home,app,env);
            Require(!Directory.Exists(newHome)&&env["TRACESOUL2_HOME"]==newHome,"显式新建实例不借用别的家目录");
            env=Fixture("legacy-data-only",out home,out app);
            var oldRole=Path.Combine(home,"old-role"); Data(oldRole,"tracesoul2-brainframe.sqlite3","synthetic");
            env=new Dictionary<string,string>{{"TRACESOUL2_DATA",oldRole}};
            RuntimePathRepair.Check(home,app,env);
            Require(env["TRACESOUL2_PLUGINS"]==Path.Combine(home,"plugins"),"仅DATA旧布局保留原默认插件位置");
            Require(!env.ContainsKey("TRACESOUL2_HOME"),"合法DATA旧布局不被擅自重解释");
            env=Fixture("missing-established",out home,out app);
            File.WriteAllText(Path.Combine(home,"control-auth.json"),"{}");
            var missing=Path.Combine(home,"missing-soul");env["TRACESOUL2_DATA"]=missing;
            MustReject(()=>RuntimePathRepair.Check(home,app,env),"已有实例目录丢失时不创建空库替代");
            Require(!Directory.Exists(missing),"错误路径未生成空目录");
            Console.WriteLine("Runtime path checks passed: verified copy migration, unchanged original paths/config/WAL, evolved destination restart, old-source write conflicts, valid mounts, update backup recovery, archive/home copies and foreign paths.");
        }
        finally {if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
}
