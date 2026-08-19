#!/bin/bash
# Sequential sweep of both shared drives. Sequential on purpose: ten workers
# hammering one Google Drive mount at once helps nobody.
cd /c/Users/zkomarnisky/GIT/SMSLIBRE
echo "=== STAAR sweep started $(date) ==="
python tools/vault_test.py \
  --root "G:/Shared drives/210600 STAAR" \
  --out analysis/staar --depth 14 --cap 200000 --workers 5 \
  --min-depth 2 --timeout 7200 --scan-timeout 21600
echo "=== STAAR done, vault sweep started $(date) ==="
python tools/vault_test.py \
  --root "G:/Shared drives/Olds College Smart Farm Vault" \
  --out analysis/vault --depth 14 --cap 200000 --workers 5 \
  --min-depth 2 --timeout 7200 --scan-timeout 21600
echo "=== ALL DONE $(date) ==="
