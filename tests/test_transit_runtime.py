"""Offline runtime and Windows child ownership regressions; never contacts public services."""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch
import urllib.error
import zipfile

SOURCE = Path(__file__).resolve().parents[1] / 'transit' / 'ardtransit.py'
spec = importlib.util.spec_from_file_location('transit_runtime', SOURCE)
transit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transit)


class RuntimeTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix='ArdTransit-runtime-test-')
        self.root = Path(self.folder.name)
        self.args = SimpleNamespace(data=str(self.root),server='https://127.0.0.1:1',capacity=2,mbps=10,diagnostics=None)
        self.agent = transit.Agent(self.args)
        self.agent.api.register = AsyncMock(return_value=None)
        self.agent.api.call = AsyncMock(return_value=dict(leases={},jobs=[]))
        pause = self.agent.pause
        self.agent.pause = lambda seconds: pause(min(seconds,.03))
        self.task = None

    async def asyncTearDown(self):
        if self.task is not None and not self.task.done():
            (self.root / 'stop.request').touch()
            await asyncio.wait_for(self.task,2)
        self.agent.job.close()
        self.folder.cleanup()

    async def wait_state(self, state, timeout=2):
        until = time.monotonic()+timeout
        while time.monotonic()<until:
            path = self.root / 'status.json'
            if path.exists():
                value = json.loads(path.read_text(encoding='utf-8'))
                if value['state']==state: return value
            await asyncio.sleep(.005)
        self.fail('State not observed: '+state)

    async def test_offline_registration_recovers_and_stops(self):
        identity = (self.root / 'identity').read_bytes()
        self.agent.api.register.side_effect = [urllib.error.URLError('offline'),None]
        self.task = asyncio.create_task(self.agent.run())
        offline = await self.wait_state('offline')
        self.assertEqual(offline['lastHeartbeat'],0)
        online = await self.wait_state('online')
        self.assertGreater(online['lastHeartbeat'],0)
        self.assertEqual((online['version'],online['build'],online['pid']),('v2.pre1','relay1',os.getpid()))
        self.assertEqual((online['capacity'],online['active'],online['mbps']),(2,0,10))
        self.assertEqual(online['endpoint'],self.agent.api.endpoint)
        self.assertEqual(identity,(self.root / 'identity').read_bytes())
        self.assertEqual(self.agent.api.register.await_count,2)
        (self.root / 'stop.request').touch()
        await asyncio.wait_for(self.task,2)
        stopped = await self.wait_state('stopped')
        self.assertEqual(stopped['lastHeartbeat'],online['lastHeartbeat'])
        self.assertFalse(list(self.root.glob('.status.json-*')))

    async def test_stop_during_registration_retry(self):
        self.agent.api.register.side_effect = urllib.error.URLError('offline')
        self.task = asyncio.create_task(self.agent.run())
        await self.wait_state('offline')
        (self.root / 'stop.request').touch()
        await asyncio.wait_for(self.task,2)
        self.assertEqual((await self.wait_state('stopped'))['lastHeartbeat'],0)
        self.agent.api.call.assert_not_awaited()

    async def test_existing_stop_request_does_not_register(self):
        (self.root / 'stop.request').touch()
        await self.agent.run()
        self.agent.api.register.assert_not_awaited()
        self.assertEqual((await self.wait_state('stopped'))['active'],0)

    async def test_heartbeat_failure_reports_offline_then_recovers(self):
        self.agent.api.call.side_effect = [OSError('network unavailable'),dict(leases={},jobs=[])]
        self.task = asyncio.create_task(self.agent.run())
        self.assertEqual((await self.wait_state('offline'))['lastHeartbeat'],0)
        self.assertGreater((await self.wait_state('online'))['lastHeartbeat'],0)
        (self.root / 'stop.request').touch()
        await asyncio.wait_for(self.task,2)

    async def test_diagnostics_error_does_not_stop_heartbeats(self):
        self.args.diagnostics = str(self.root / 'nonexistent' / 'report.zip')
        self.task = asyncio.create_task(self.agent.run())
        await self.wait_state('online')
        until = time.monotonic()+2
        while self.agent.api.call.await_count<2 and time.monotonic()<until: await asyncio.sleep(.01)
        self.assertGreaterEqual(self.agent.api.call.await_count,2)
        self.assertFalse(self.task.done())
        self.assertTrue(any(row['event']=='diagnostics-error' for row in transit.events))

    async def test_diagnostic_zip_atomic_failure_preserves_last_copy(self):
        path = self.root / 'diagnostics.zip'
        self.assertTrue(self.agent.diagnostics(path))
        before = path.read_bytes()
        with patch.object(transit.os,'replace',side_effect=PermissionError('locked output')):
            self.assertFalse(self.agent.diagnostics(path))
        self.assertEqual(before,path.read_bytes())
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(set(archive.namelist()),{'status.json','events.jsonl'})
            self.assertNotIn((self.root / 'identity').read_bytes().hex(),archive.read('status.json').decode())
        self.assertFalse(list(self.root.glob('.diagnostics.zip-*')))

    async def test_directory_key_change_is_rejected_and_preserved(self):
        pinned = '11'*32
        (self.root / 'directory-public-key').write_text(pinned)
        self.agent.api.register = transit.Api.register.__get__(self.agent.api)
        self.agent.api.call.side_effect = [{},{'endpoint':'22'*32}]
        with self.assertRaisesRegex(transit.DirectoryTrustError,'signing key changed'):
            await self.agent.run()
        self.assertEqual((self.root / 'directory-public-key').read_text(),pinned)
        self.assertEqual(self.agent.api.call.await_count,2)
        self.assertEqual((await self.wait_state('stopped'))['lastHeartbeat'],0)

    async def test_file_logging_is_bounded_and_keeps_stdout(self):
        handler = transit.configure_log(self.root)
        try:
            self.assertEqual((handler.maxBytes,handler.backupCount),(2*1024*1024,3))
            handler.maxBytes = 180
            with patch('builtins.print') as output:
                for index in range(10): transit.log('rotation-test',sequence=index,text='x'*80)
                self.assertEqual(output.call_count,10)
            paths = list(self.root.glob('relay.log*'))
            self.assertEqual(len(paths),4)
            self.assertTrue(all(path.stat().st_size<250 for path in paths))
            self.assertIn('rotation-test',(self.root / 'relay.log').read_text())
        finally:
            transit.runtime_log.removeHandler(handler)
            handler.close()

    async def test_managed_spawn_is_killed_on_job_close(self):
        if os.name!='nt': self.skipTest('Windows process ownership test')
        process = await self.agent.spawn(sys.executable,'-c','import time; time.sleep(60)',creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            self.agent.job.close()
            self.assertIsNotNone(await asyncio.wait_for(process.wait(),5))
        finally:
            if process.returncode is None: process.kill()
            await process.wait()

    async def test_main_fatal_initialization_is_logged(self):
        creation = dict(creationflags=subprocess.CREATE_NO_WINDOW) if os.name=='nt' else {}
        process = await asyncio.create_subprocess_exec(sys.executable,str(SOURCE),'--ard',str(self.root/'missing-ard.exe'),
            '--data',str(self.root/'fatal-data'),stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,**creation)
        await asyncio.wait_for(process.communicate(),8)
        self.assertNotEqual(process.returncode,0)
        self.assertIn('"event": "fatal"',(self.root/'fatal-data'/'relay.log').read_text(encoding='utf-8'))


@unittest.skipUnless(os.name=='nt','Windows process ownership test')
class WindowsJobTest(unittest.TestCase):
    def test_close_kills_owned_child_and_preserves_unrelated_process(self):
        creation = dict(creationflags=subprocess.CREATE_NO_WINDOW)
        child = subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)'],**creation)
        unrelated = subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)'],**creation)
        job = transit.ChildJob()
        try:
            job.assign(child.pid)
            job.close()
            self.assertIsNotNone(child.wait(timeout=5))
            self.assertIsNone(unrelated.poll())
        finally:
            job.close()
            for process in (child,unrelated):
                if process.poll() is None: process.kill()
                process.wait(timeout=5)

    def test_force_killed_owner_cleans_child(self):
        import ctypes
        from ctypes import wintypes
        api = ctypes.WinDLL('kernel32',use_last_error=True)
        api.OpenProcess.argtypes = [wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
        api.OpenProcess.restype = wintypes.HANDLE
        api.WaitForSingleObject.argtypes = [wintypes.HANDLE,wintypes.DWORD]
        api.WaitForSingleObject.restype = wintypes.DWORD
        api.CloseHandle.argtypes = [wintypes.HANDLE]
        api.CloseHandle.restype = wintypes.BOOL
        command = '''import importlib.util,json,pathlib,subprocess,sys,time
spec=importlib.util.spec_from_file_location('transit',sys.argv[1]); module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
job=module.ChildJob()
child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)'],creationflags=subprocess.CREATE_NO_WINDOW)
job.assign(child.pid)
pathlib.Path(sys.argv[2]).write_text(json.dumps({'pid':child.pid}))
time.sleep(60)
'''
        with tempfile.TemporaryDirectory(prefix='ArdTransit-job-test-') as folder:
            ready = Path(folder) / 'ready.json'
            parent = subprocess.Popen([sys.executable,'-c',command,str(SOURCE),str(ready)],creationflags=subprocess.CREATE_NO_WINDOW,
                stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            handle = None
            try:
                deadline = time.monotonic()+8
                while not ready.exists() and time.monotonic()<deadline and parent.poll() is None: time.sleep(.03)
                if not ready.exists():
                    parent.kill()
                    self.fail('Job fixture failed: '+parent.communicate(timeout=3)[1].decode(errors='replace'))
                child_pid = json.loads(ready.read_text())['pid']
                handle = api.OpenProcess(0x100000,False,child_pid)
                self.assertTrue(handle)
                parent.kill()
                parent.wait(timeout=5)
                self.assertEqual(api.WaitForSingleObject(handle,5000),0,'owned child survived forced owner termination')
            finally:
                if parent.poll() is None: parent.kill()
                parent.communicate(timeout=5)
                if handle: api.CloseHandle(handle)


if __name__=='__main__': unittest.main()
