# SirenSpy (PS3/RPCS3)

**State:** 🚧 EXTREMELY Unfinished – PRE-PRE-PRE-Alpha  
**Status:** Stuck on Loading Profile screen (SSL Cert Verification)

Private C# Gamespy server – made specifically for *Gotham City Impostors*.  
(Yes, the code is bad (Everything is hardcoded). No, I’m not fixing it yet.)

> ⚠️ Only tested using **RPCS3**.

---

## Setup

**Step 1:** In RPCS3, go to:  
`RPCS3 → Config → Network → DNS → 127.0.0.1`

From here, get a DNS Redirector (I use Acrylic DNS) and redirect:
`*.gamespy.com` to `127.0.0.1` | `*.gamespy.net` to `127.0.0.1` | `*.agoragames.com` to `127.0.0.1`

**Step 2:** Generate .ELF File:  
`RPCS3 → Utilities → Decrypt PS3 Binaries → Eboot.bin`
> Eboot.bin is located in Gotham City Impostors → USDIR directory 

**Step 3:** Drag .ELF file to Hex Editor and replace:  
`"https://%s.api.gamespy"` with `"http://%s.api.gamespy`

Replace Public Key
`BF05D63E93751AD4A59A4A7389CF0BE8A22CCDEEA1E7F12C062D6E194472EFDA5184CCECEB4FBADF5EB1D7ABFE91181453972AA971F624AF9BA8F0F82E2869FB7D44BDE8D56EE50977898F3FEE75869622C4981F07506248BD3D092E8EA05C12B2FA37881176084C8F8B8756C4722CDC57D2AD28ACD3AD85934FB48D6B2D2027`

To Unispy Key

`aefb5064bbd1eb632fa8d57aab1c49366ce0ee3161cbef19f2b7971b63b811790ecbf6a47b34c55f65a0766b40c261c5d69c394cd320842dd2bccba883d30eae8fdba5d03b21b09bfc600dcb30b1b2f3fbe8077630b006dcb54c4254f14891762f72e7bbfe743eb8baf65f9e8c8d11ebe46f6b59e986b4c394cfbc2c8606e29f`

Replace Exponent Key (should be right beside the Unispy key)
`010001` to `000001`

> 🛠️ Important: Make sure to add a byte count of 1 for each string replaced so the file size stays the same.

**Final Step:** Boot modified .ELF to run Gotham City Impostors, and run SirenSpy along with it.


That’s it for now. If it breaks, i'ts expected.

## What needs fixing
The game expects a valid signed certificate which we don't have, so we need to bypass the SSL Verification method in the EBOOT.BIN, possible with GHIDRA or IDA.

## Thanks
Unispy SDK/Code - Big thanks for getting past remote auth
