import json, os, secrets, socket, subprocess, sys, tempfile, time
from pathlib import Path
import grpc
from google.protobuf import descriptor_pb2, descriptor_pool, message_factory

host, bridge, descriptors = (Path(arg).resolve(strict=True) for arg in sys.argv[1:])
pool = descriptor_pool.DescriptorPool()
for f in descriptor_pb2.FileDescriptorSet.FromString(descriptors.read_bytes()).file:
    pool.Add(f)

def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]

with tempfile.TemporaryDirectory(prefix="oadm-ordinary-key-") as directory:
    path = Path(directory)
    gport, mport = port(), port()
    assert gport != mport
    log = (path/"host.log").open("wb")
    process = subprocess.Popen([host, f"--Oadm:DataDir={path}",
        f"--Oadm:ListenUrl=https://127.0.0.1:{gport}", "--Oadm:PluginDir=plugins"],
        cwd=host.parent, stdout=log, stderr=subprocess.STDOUT)
    try:
        tls=path/"server-tls.json"
        for _ in range(120):
            if tls.exists(): break
            assert process.poll() is None
            time.sleep(.25)
        payload=json.loads(tls.read_bytes())
        pem=next(v for k,v in payload.items() if k.lower()=="certificatepem")
        with grpc.secure_channel(f"127.0.0.1:{gport}",grpc.ssl_channel_credentials(pem.encode())) as channel:
            grpc.channel_ready_future(channel).result(30)
            metadata=()
            def rpc(service, method, **fields):
                d=pool.FindServiceByName("oadm.v1."+service).methods_by_name[method]
                req=message_factory.GetMessageClass(d.input_type)(**fields)
                route=channel.unary_unary(f"/{d.containing_service.full_name}/{method}",
                    request_serializer=lambda v:v.SerializeToString(),
                    response_deserializer=message_factory.GetMessageClass(d.output_type).FromString)
                return route(req,metadata=metadata,timeout=15)
            login=rpc("AuthService","CreateFirstAdmin",user_name="Lab",password=secrets.token_urlsafe(25)+"A1!")
            metadata=(("authorization","Bearer "+login.token),)
            assert not rpc("DeviceService", "List").devices
            mcp=rpc("McpService","Get")
            loop=next(i for i in mcp.interfaces if any(a.startswith("127.") for a in i.addresses))
            rpc("McpService","Set",enabled=True,port=mport,listen_on=loop.id)
            for _ in range(40):
                status=rpc("McpService","Get")
                if status.running and status.has_ca_certificate: break
                time.sleep(.25)
            assert status.running and status.has_ca_certificate
            ca=path/"root-ca.pem"
            ca.write_text(rpc("McpService","GetCaCertificate").pem)
            key=rpc("AccessKeyService","Create",name="Bridge proof",role_id="00000000-0000-0000-0000-000000000003",
                    days=30,all_devices=True)
            def call(method,params=None,ca_path=ca):
                request={"jsonrpc":"2.0","id":1,"method":method,"params":params or {}}
                result=subprocess.run([bridge,f"https://localhost:{mport}/mcp"],
                    input=json.dumps(request)+"\n",text=True,capture_output=True,timeout=30,
                    env={**os.environ,"OADM_MCP_CA":str(ca_path),"OADM_MCP_KEY":key.secret})
                assert key.secret not in result.stdout+result.stderr and result.returncode==0
                return json.loads(result.stdout)
            try:
                assert call("initialize",{"protocolVersion":"2025-11-25"})["result"]["serverInfo"]["name"]=="OADM"
                listed=call("tools/list")
                assert "result" in listed
                tools={t["name"] for t in listed["result"]["tools"]}
                assert "oadm_server_info" in tools and "oadm_task_run" not in tools
                reply=call("tools/call",{"name":"oadm_server_info","arguments":{}})
                assert "result" in reply and not reply["result"].get("isError")
            finally:
                rpc("AccessKeyService","Delete",id=key.key.id)
            revoked=call("tools/list")
            assert revoked["error"]["message"]=="The OADM access key was refused."
            print(json.dumps(dict(realOadm=True,ordinaryViewerKey=True,toolsListed=len(tools),
                                  readToolPassed=True,revocationVerified=True,deviceCount=0)))
    finally:
        process.terminate()
        try: process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        log.close()
