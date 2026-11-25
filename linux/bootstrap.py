"""Generate guest-local service settings. Secrets survive retries inside this VM."""
import base64
import hashlib
import hmac
import json
import re
import socket
import ssl
import ipaddress
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import uuid
from urllib.request import urlopen, Request

d = json.loads(Path(os.environ.get("CSWEET_DEPLOYMENT_FILE", "/etc/csweet-hosting/deployment.json")).read_text())
# Operator-provisioned TCP/TLS passthrough. Each tenant has its own hostname,
# TLS key and Office certificate issuer; the web tunnel cannot forward client certificates.
office_dir = Path("/etc/csweet-hosting/office-gateway")
office_config = office_dir / "gateway.json"
office = json.loads(office_config.read_text()) if office_config.is_file() else None
if office is not None:
    host = "office-" + uuid.UUID(d["deployment"]).hex + ".c-sweet.app"
    if office["hostname"] != host:
        sys.exit("Office gateway does not belong to this deployment")
    bind = str(ipaddress.IPv4Address(office["bindAddress"]))
    if not all(re.fullmatch(r"sha256:[0-9a-f]{64}", office[k]) for k in ("builderDigest", "runtimeDigest")):
        sys.exit("Certified builder and runtime image digests are required")
    for filename in ("tls.pem", "tls.key", "office-issuer.pfx"):
        if not (office_dir / filename).is_file():
            sys.exit("Missing Office gateway material: " + filename)

if sys.argv[1:] == ["verify-office"]:
    if office is None:
        sys.exit("Office gateway has not been provisioned")
    ctx = ssl.create_default_context()
    ctx.set_alpn_protocols(["h2"])
    with socket.create_connection((host, 443), timeout=10) as conn:
        with ctx.wrap_socket(conn, server_hostname=host) as tls:
            if tls.selected_alpn_protocol() != "h2":
                sys.exit("Public Office gateway did not negotiate HTTP/2")
    keys = json.loads(Path("secrets.json").read_text())
    public_key = subprocess.check_output(["openssl", "pkey", "-inform", "DER", "-pubout", "-outform", "DER"],
                                         input=base64.b64decode(keys["assignment"], validate=True))
    with urlopen("https://" + host + "/api/offices/assignment-trust", timeout=10) as response:
        trust = json.load(response)
    if not hmac.compare_digest(base64.b64encode(public_key).decode(), trust["assignmentVerificationPublicKeyBase64"]):
        sys.exit("Public gateway did not present this tenant's assignment key")
    sys.exit(0)

if sys.argv[1:] == ["verify"]:
    import urllib.error
    for attempt in range(60):
        try:
            nonce = secrets.token_hex(16)
            url = "http://127.0.0.1:8181/api/auth/hosted/readiness?nonce="+nonce
            with urlopen(Request(url, headers={"Host":d["hostname"]}), timeout=5) as res:
                signature = json.load(res)["proof"]
            message = "\n".join([d["deployment"],d["owner"],d["hostname"],d["release"],nonce]).encode()
            expected = hmac.new(bytes.fromhex(d["proofKey"]),message,hashlib.sha256).hexdigest()
            if hmac.compare_digest(signature, expected):
                sys.exit(0)
        except (OSError, ValueError, KeyError):
            pass
        time.sleep(2)
    sys.exit("Headquarters owner reservation or database check failed")

os.umask(0o077)
keyfile = Path("secrets.json")
if not keyfile.exists():
    values = {name:base64.b64encode(os.urandom(32)).decode() for name in ("git","broker","database")}
    der = subprocess.check_output(["openssl","genpkey","-algorithm","EC","-pkeyopt","ec_paramgen_curve:P-256"])
    pkcs8 = subprocess.check_output(["openssl","pkcs8","-topk8","-nocrypt","-outform","DER"],input=der)
    values["assignment"] = base64.b64encode(pkcs8).decode()
    Path("secrets.tmp").write_text(json.dumps(values))
    os.replace("secrets.tmp",keyfile)
keys = json.loads(keyfile.read_text())
services = {"postgres":{"image":"postgres:17-alpine","restart":"unless-stopped","environment":{
    "POSTGRES_DB":"csweet","POSTGRES_USER":"csweet","POSTGRES_PASSWORD":keys["database"]},
    "volumes":["database:/var/lib/postgresql/data"],"healthcheck":{"test":["CMD","pg_isready","-U","csweet","-d","csweet"],
    "interval":"3s","timeout":"3s","retries":30}}}
volumes = {"database":{}}
names = {"api":"Api","migrate":"Migrator","worker":"WorkerHost","agent":"AgentHost",
         "git":"GitHost","provisioner":"SourceControlProvisionerHost","gateway":"ExecutionGateway"}
for alias, suffix in names.items():
    app = "CSweet."+suffix
    env = {"ConnectionStrings__csweet":"Host=postgres;Database=csweet;Username=csweet;Password="+keys["database"],
           "ASPNETCORE_URLS":"http://+:8080",
           "CSweet__AgentRuntime__AgentHostBroker__BaseUrl":"http://agent:8080",
           "CSweet__SourceControl__GitHostBaseUrl":"http://git:8080",
           "CSweet__SourceControl__ProvisionerHostBaseUrl":"http://provisioner:8080",
           "CSweet__SourceControl__CoreBrokerBaseUrl":"http://api:8181",
           "CSweet__Secrets__FilePath":"/state/provider-secrets.json"}
    if alias in ("api","worker","agent","git","provisioner"):
        env.update(CSweet__SourceControl__TrustedServiceKeyId="core",CSweet__SourceControl__TrustedServiceKeyBase64=keys["git"])
    if alias in ("git","provisioner"):
        env.update(TrustedServiceAuthentication__KeyId="core",TrustedServiceAuthentication__SharedKeyBase64=keys["git"])
    if alias == "git":
        env.update(CSweet__SourceControl__Storage__RepositoryRoot="/state/repositories",
                   CSweet__SourceControl__Storage__TemporaryRoot="/state/tmp")
    if alias in ("api","agent"):
        env.update(CSweet__SourceControl__AgentBrokerKeyId="agenthost",CSweet__SourceControl__AgentBrokerKeyBase64=keys["broker"])
    if alias == "gateway":
        env["CSweet__ExecutionGateway__AssignmentSigningPrivateKeyPkcs8Base64"] = keys["assignment"]
    if alias == "api":
        env.update(ASPNETCORE_URLS="http://+:8181", Installation__ServePackagedWeb="true",
                   MobileRelay__Enabled="true", MobileRelay__Origin="https://mobile.c-sweet.app/",
                   MobileRelay__RouteId="hq-" + uuid.UUID(d["deployment"]).hex, MobileShell__WebRoot="/srv/web",
                   Installation__LinkingEnabled="true",Installation__Issuer="https://account.c-sweet.com/",
                   Installation__ReservedOwnerSubject=d["owner"],Installation__DeploymentId=d["deployment"],
                   Installation__Release=d["release"],Installation__Hostname=d["hostname"],Installation__ReadinessKey=d["proofKey"])
    volumes[alias]={}
    services[alias]={"image":os.environ.get("CSWEET_LOCAL_HQ_IMAGE", d["release"]), "command":["/srv/"+app+"/"+app+".dll"],
                     "working_dir":"/srv/"+app, "environment":env, "volumes":[alias+":/state"],
                     "restart":"no" if alias=="migrate" else "unless-stopped"}
    if "CSWEET_LOCAL_HQ_IMAGE" in os.environ:
        services[alias]["pull_policy"] = "never"
if office is not None:
    services["gateway"]["ports"] = [bind + ":7443:7443"]
    tls_env = services["gateway"]["environment"]
    tls_env.update(Kestrel__Endpoints__Office__Url="https://+:7443",
                   Kestrel__Endpoints__Office__Protocols="Http1AndHttp2",
                   Kestrel__Endpoints__Office__Certificate__Path="/run/office/tls.pem",
                   Kestrel__Endpoints__Office__Certificate__KeyPath="/run/office/tls.key")
    for f in ("tls.pem", "tls.key"):
        services["gateway"]["volumes"].append(str(office_dir / f) + ":/run/office/" + f + ":ro")
    for alias in ("api", "gateway"):
        services[alias]["volumes"].append(str(office_dir / "office-issuer.pfx") + ":/run/office/issuer.pfx:ro")
        services[alias]["environment"].update(CSweet__ExecutionNodeCertificates__AuthorityPfxPath="/run/office/issuer.pfx",
                                             CSweet__ExecutionNodeCertificates__AuthorityPfxPassword=office["issuerPassword"])
    for alias in ("api", "gateway", "agent", "worker"):
        services[alias]["environment"].update(CSweet__ExecutionGateway__PublicUrl="https://" + host,
                                             CSweet__ExecutionFleet__PublicLaunchEnabled="true",
                                             CSweet__AgentRuntime__RequiredCertificationSuiteVersion=office["certificationSuite"],
                                             CSweet__AgentRuntime__BuilderGuestImageDigest=office["builderDigest"],
                                             CSweet__AgentRuntime__RuntimeGuestImageDigest=office["runtimeDigest"])
services["api"]["ports"]=["127.0.0.1:8181:8181"]
services["connector"]={"image":"cloudflare/cloudflared:latest","restart":"unless-stopped","network_mode":"service:api",
                       "environment":{"TUNNEL_TOKEN":d["tunnelToken"]},"command":["tunnel","--no-autoupdate","run"]}
Path("compose.json").write_text(json.dumps({"name":"csweet","services":services,"volumes":volumes},indent=2))
