# Release Hosting to the Mac Studio

Hosting runs as its own Docker Compose project, `csweet-hosting`. It listens on the Mac's loopback port 8198. The private operator API accepts a test order directly. A live Market website and Stripe payment are unnecessary for this test. A real provisioning call creates a guest VM, a Cloudflare tunnel and a DNS record.

## Set up the Mac once

1. In the local GitHub repository that owns Hosting, enable Actions and use `main` as the release branch. The **Hosting build and Mac Studio release** workflow verifies pull requests and pushes to main on GitHub's Linux ARM64 runner. A manual run from main also publishes and deploys a version. Each version is a GitHub Release containing the tested Linux ARM64 Docker image and its matching Compose file.
2. Register a dedicated self-hosted macOS ARM64 runner for the release job with the custom label `csweet-hosting-deploy`. Run it as the same Mac user that has access to Docker Desktop and the NAS mount. Verify `docker info` and `docker compose version` from that user's runner environment. Configure the GitHub environment `mac-studio` for deployments from protected main. Use a private, trusted repository or a restricted runner group scoped to the main-branch deployment workflow. GitHub-hosted jobs handle PR code. [GitHub runner routing](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/use-in-a-workflow)
3. Create a service directory outside the runner's checkout. The default is `$HOME/csweet-hosting`. To choose another absolute path, set the environment variable **HOSTING_DEPLOY_DIR** in the GitHub environment's **Variables** section. Under the directory create `secrets/` and copy the [configuration example](../deploy/hosting.env.example) to `hosting.env`. Restrict both directories to the Mac service account. Fill in the actual NAS share paths, certificate hostname, template, Cloudflare account and zone IDs, and the published Headquarters image digest. The Hosting release version and the guest Headquarters release are independent.
4. In `secrets/`, place `hosting-nas-ca.pem`, `hosting-nas-key.txt`, and `hosting-cloudflare-token.txt` as described in the [TrueNAS setup guide](../README.md). Add a strong random bearer key in `hosting-service-key.txt`. For an existing Market pilot use its current service key. Each file must be readable by the container's UID 10001. On Docker Desktop, one option is mode 0444 on these individually mounted files beneath owner-only directories on the Mac. Keep the keys out of GitHub Actions. The deploy job transfers the application artifact only.
5. If migrating an existing Market-overlay provisioner, stop **only** that service with the original Compose command (`lab stop provisioner`) before first deployment. Set `HOSTING_STATE_VOLUME` to its existing Docker volume. The default is `csweet-market_hosting-state`. Set `HOSTING_POOL_ID` and the NAS settings to the original values so pending orders can resume. New standalone installations can use that same default volume name. Ensure Docker Desktop can read the mounted media share and the container can write to it.

## Publish a version

Open **Actions → Hosting build and Mac Studio release → Run workflow**, select **main**, and supply an unused version such as `v0.1.0`. The workflow builds the test stage, builds the native ARM64 runtime image and tests authenticated HTTP in that image. It uploads the exact image as a versioned GitHub Release. Release asset visibility follows the repository's visibility. The Mac job downloads the verified artifact, checks the checksums and revision, loads the image locally and runs only the Hosting Compose project. A successful deployment verifies the running image ID, health endpoint and authenticated request handling before recording `current-version`.

If publication succeeded and Mac deployment failed, use **Re-run failed jobs** on that run. Check the runner is online, Docker Desktop is running, secret files and the NAS mount are readable, and the configured port is free. The installer attempts to restore the previously healthy version if the replacement fails. The release directories retain Compose files and image IDs for manual rollback. From any downloaded release bundle on the Mac run `bash deploy.sh --rollback v0.1.0` to select a previously installed version. Keep the earlier Docker image locally for rollback. The SQLite volume stays attached across these operations.

An operator can also download all assets of an existing GitHub Release into a new directory on the Mac, verify them with `shasum -a 256 -c SHA256SUMS` and run `bash deploy.sh` there. Both paths use the same installer. The NAS and Cloudflare credentials remain on the Mac.

## Validate the API from this PC

Open an SSH forward using the Mac login and its Tailscale hostname. Keep it open during the test.

```sh
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:8198:127.0.0.1:8198 YOUR_MAC_USER@macstudio.taildfd07a.ts.net
```

Keep a private copy of the operator bearer key on the test machine. Use PowerShell 7 from the Hosting checkout. The health action confirms both reachability and the credential with a request that allocates nothing.

```powershell
./scripts/Test-HostingApi.ps1 -Action Health -KeyFile ./private/hosting-service-key.txt
```

To initialize the NAS before its first real order, use the active release and the settings from the service directory on the Mac. Run these in the same shell.

```sh
root=${HOSTING_DEPLOY_DIR:-"$HOME/csweet-hosting"}
release=$(cat "$root/current-version")
hosting() {
  HOSTING_IMAGE="$(cat "$root/releases/$release/image-id")" docker compose --project-directory "$root" \
    --env-file "$root/hosting.env" -p csweet-hosting -f "$root/releases/$release/compose.hosting.yml" "$@"
}
hosting run --rm --no-deps provisioner preflight
```

Use `hosting run --rm --no-deps provisioner prepare-template` with the verified image, digest, dataset and NIC from the [TrueNAS setup guide](../README.md). When the template reports **STOPPED**, submit a test purchase. Supply the intended buyer's Market account subject if you plan to activate Headquarters. The direct API test simulates payment confirmation and creates no invoice or Market order. Signing in to the resulting Headquarters still uses its configured account issuer.

```powershell
./scripts/Test-HostingApi.ps1 -Action Provision -OwnerId 'YOUR_TEST_ACCOUNT_ID' -OrderFile ./private/nas-test.json -KeyFile ./private/hosting-service-key.txt
```

The client saves the exact request before sending it, repeats the authenticated PUT until Ready and prints the assigned HTTPS URL. If interrupted, repeat the command with the same order file. The client verifies the returned order ID and hostname. A new file creates a new allocation. To **delete this test VM and its data**, retire that same order explicitly.

```powershell
./scripts/Test-HostingApi.ps1 -Action Retire -OrderFile ./private/nas-test.json -KeyFile ./private/hosting-service-key.txt
```

## Connect Market later

The standalone service also listens as `provisioner:8080` on the `csweet-hosting-api` Docker network. When ready for checkout, add Market's `compose.hosting-api.yml` to its production Compose invocation, supply the matching service key and Stripe test settings in Market, and update its web container. Use this overlay in place of the legacy hardware-lab overlay. Hosting and Market can then release independently.
