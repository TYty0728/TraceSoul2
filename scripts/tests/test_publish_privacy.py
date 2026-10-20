"""Publish checks must reject forced-added runtime data without echoing its contents."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(shutil.which('pwsh') and shutil.which('git'), 'PowerShell and Git are required')
class PublishPrivacyChecks(unittest.TestCase):
    def test_runtime_data_and_service_defaults_are_rejected(self):
        source=Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory(prefix='tracesoul-publish-privacy-') as folder:
            root=Path(folder)
            (root/'scripts').mkdir()
            shutil.copyfile(source/'scripts/Test-PublishSafety.ps1',root/'scripts/Test-PublishSafety.ps1')
            subprocess.run(['git','init','--quiet',str(root)],check=True,capture_output=True)
            (root/'resources').mkdir()
            (root/'resources/identity_cards.json').write_text(json.dumps({'cards':[{'slot':'self','body':'我是{assname}'}]}),encoding='utf-8')
            subprocess.run(['git','-C',str(root),'add','.'],check=True,capture_output=True)
            def check():
                return subprocess.run(['pwsh','-NoProfile','-File',str(root/'scripts/Test-PublishSafety.ps1')],
                    capture_output=True,text=True,encoding='utf-8')
            self.assertEqual(check().returncode,0,'generic empty identity templates must remain publishable')
            marker='private-fixture-marker-not-for-log'
            cases={name:marker for name in ['plugins_data/sticker/example.png','souls/example/profile.json',
                'plugin-data/example.json','emoji_index.json','owner-voiceprint.json','local-voice-client.json',
                'access.json','speech.json','wake.json','wifi-client-backup.json']}
            cases['ExternalPlugins/QqTts/plugin.json']=json.dumps({'api_url':'https://relay.example/v1/audio/speech','api_key':''})
            cases['ExternalPlugins/QqTts/QqTtsPlugin.cs']='private string configApiUrl = "https://relay.example/v1/audio/speech";'
            for name,body in cases.items():
                with self.subTest(name=name):
                    path=root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_text(body,encoding='utf-8')
                    subprocess.run(['git','-C',str(root),'add','-f',name],check=True,capture_output=True)
                    result=check()
                    self.assertNotEqual(result.returncode,0)
                    self.assertNotIn(marker,result.stdout+result.stderr)
                    self.assertNotIn('https://relay.example',result.stdout+result.stderr)
                    subprocess.run(['git','-C',str(root),'rm','--cached','--quiet','--',name],check=True,capture_output=True)
                    path.unlink()
            self.assertEqual(check().returncode,0)


if __name__=='__main__':unittest.main()
