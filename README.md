# SirenSpy (PS3/RPCS3)

**State:** 🚧 EXTREMELY Unfinished – PRE-PRE-PRE-Alpha  
**Status:** Stuck on Loading Profile screen (Gets past auth login)

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
> 🛠️ Important: Make sure to add a byte count of 1 for each string replaced so the file size stays the same.

**Final Step:** Boot modified .ELF to run Gotham City Impostors, and run SirenSpy along with it.


That’s it for now. If it breaks, i'ts expected.

## Thanks
Unispy SDK/Code - Big thanks for getting past remote auth functions
