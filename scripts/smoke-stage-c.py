"""One real stdio MCP install/update lifecycle in an isolated service and Git project."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / 'artifacts' / ('stage-c-smoke-' + uuid.uuid4().hex)
WORK.mkdir(parents=True)
SERVICE = WORK / 'service'
PROJECT = WORK / 'consumer'
shutil.copytree(ROOT / 'artifacts/server-0.3.0', SERVICE)
PROJECT.mkdir()
SERVER = 'http://127.0.0.1:5195'
(SERVICE / 'appsettings.Local.json').write_text(json.dumps({
    'Urls': SERVER, 'Repository': {'DataDirectory': str(WORK / 'data'), 'RequireLogin': False}
}), encoding='utf-8')
events = []

def command(*args):
    result = subprocess.run(args, cwd=PROJECT, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=90)
    events.append({'command': list(args), 'exitCode': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr})
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout

def api(method, route, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(SERVER + '/api/' + route, data, {'Content-Type': 'application/json'}, method=method)
    with urllib.request.urlopen(req, timeout=15) as response:
        raw = response.read()
        return json.loads(raw) if raw else None

service_log = (WORK / 'service.log').open('w', encoding='utf-8')
mcp_log = (WORK / 'mcp.log').open('w', encoding='utf-8')
service = subprocess.Popen([str(SERVICE / 'ModuleRepository.Web.exe')], cwd=SERVICE, stdout=service_log, stderr=subprocess.STDOUT)
mcp = None
try:
    for attempt in range(60):
        if service.poll() is not None:
            raise RuntimeError('Isolated service failed to start; inspect service.log.')
        try:
            api('GET', 'repository')
            break
        except OSError:
            time.sleep(.25)
    else:
        raise RuntimeError('Service startup timeout.')
    category = api('POST', 'categories', {'name': 'MCP integration'})
    module = 'mcp-score'
    api('POST', 'modules', {'id': module, 'metadata': {'name': 'MCP score', 'summary': 'Actual agent tool consumption',
        'language': 'C#', 'environment': '.NET', 'categoryId': category['id'], 'documentation': 'Score.Value() returns current score.'}})
    def publish(version, score):
        detail = api('GET', 'modules/' + module)
        api('POST', 'modules/' + module + '/snippet', {'path': 'Score.cs',
            'content': 'public static class Score { public static int Value() => ' + str(score) + '; }',
            'expectedDraftRevision': detail['draftRevision']})
        detail = api('GET', 'modules/' + module)
        api('POST', 'modules/' + module + '/publish', {'version': version, 'changeNotes': 'MCP smoke', 'expectedDraftRevision': detail['draftRevision']})
    publish('1.0.0', 42)
    (PROJECT / 'Consumer.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
    (PROJECT / 'Program.cs').write_text('System.Console.WriteLine(Score.Value()); return Score.Value() == int.Parse(args[0]) ? 0 : 1;')
    (PROJECT / '.gitignore').write_text('bin/\nobj/\n')
    command('git', 'init', '-b', 'codex/mcp-smoke')
    command('git', 'add', '.')
    command('git', 'commit', '-m', '建立MCP消费工程基线')
    env = dict(os.environ, MODULEREPO_SERVER=SERVER)
    env.pop('MODULEREPO_TOKEN', None)
    mcp = subprocess.Popen([str(ROOT / 'artifacts/mcp-smoke-0.3.0/module-repo-mcp.exe')],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=mcp_log, text=True, encoding='utf-8', env=env)
    incoming = queue.Queue()
    def read_stdout():
        for line in mcp.stdout:
            incoming.put(line)
    threading.Thread(target=read_stdout, daemon=True).start()
    serial = 0
    def rpc(method, params, notification=False):
        global serial
        serial += 1
        request = {'jsonrpc': '2.0', 'method': method, 'params': params}
        if not notification:
            request['id'] = serial
        mcp.stdin.write(json.dumps(request) + '\n')
        mcp.stdin.flush()
        if notification:
            return
        while True:
            response = json.loads(incoming.get(timeout=45))
            if response.get('id') == serial:
                events.append({'request': request, 'response': response})
                assert 'error' not in response, response
                return response['result']
    rpc('initialize', {'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'stage-c-smoke', 'version': '1.0'}})
    rpc('notifications/initialized', {}, True)
    names = {t['name'] for t in rpc('tools/list', {})['tools']}
    assert names == {'module_search', 'module_get', 'module_list_installed', 'module_check_updates', 'module_plan_apply', 'module_apply'}, names
    def tool(name, **args):
        result = rpc('tools/call', {'name': name, 'arguments': args})
        assert not result.get('isError'), result
        payload = json.loads(next(c['text'] for c in result['content'] if c['type'] == 'text'))
        assert payload['success'], payload
        return payload
    assert tool('module_search', query='MCP', language='C#')['modules']
    assert tool('module_get', moduleId=module)['manifest']['version'] == '1.0.0'
    assert not tool('module_list_installed', project=str(PROJECT))['state']['installations']
    plan = tool('module_plan_apply', project=str(PROJECT), moduleId=module, target='Reusable/Score')
    assert not (PROJECT / '.module-repository').exists()
    installed = tool('module_apply', planId=plan['planId'])
    command('dotnet', 'run', '--project', 'Consumer.csproj', '-c', 'Release', '--', '42')
    command('git', 'add', '.')
    command('git', 'commit', '-m', '通过MCP安装模块并实际调用')
    (PROJECT / 'Reusable/Score/ProjectExtra.cs').write_text('// project-owned file')
    publish('1.1.0', 84)
    assert tool('module_check_updates', project=str(PROJECT))['updates'][0]['status'] == 'updateAvailable'
    plan = tool('module_plan_apply', project=str(PROJECT), installationId=installed['installationId'])
    updated = tool('module_apply', planId=plan['planId'])
    assert updated['version'] == '1.1.0'
    command('dotnet', 'run', '--project', 'Consumer.csproj', '-c', 'Release', '--', '84')
    state = tool('module_list_installed', project=str(PROJECT))['state']
    assert state['installations'][0]['version'] == '1.1.0' and not state['pendingOperations']
    assert tool('module_check_updates', project=str(PROJECT))['updates'][0]['status'] == 'upToDate'
    assert (PROJECT / 'Reusable/Score/ProjectExtra.cs').read_text() == '// project-owned file'
    (WORK / 'update.diff').write_text(command('git', 'diff'), encoding='utf-8')
    (WORK / 'summary.json').write_text(json.dumps({'success': True, 'project': str(PROJECT), 'tools': sorted(names), 'outputs': [42, 84], 'version': '1.1.0'}, indent=2), encoding='utf-8')
    print(json.dumps({'success': True, 'evidence': str(WORK)}, ensure_ascii=False))
finally:
    for process in (mcp, service):
        if process is not None and process.poll() is None:
            process.terminate()
            process.wait(timeout=15)
    service_log.close()
    mcp_log.close()
    (WORK / 'events.json').write_text(json.dumps(events, ensure_ascii=False, indent=2), encoding='utf-8')
