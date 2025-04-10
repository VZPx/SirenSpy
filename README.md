# SirenSpy (PS3/RPCS3)

**State:** 🚧 EXTREMELY Unfinished – PRE-PRE-PRE-Alpha  
**Status:** Can’t even get past the main menu.  

Private C# Gamespy server – made specifically for *Gotham City Impostors*.  
(Yes, the code is bad. No, I’m not fixing it yet.)

> ⚠️ Only tested using **RPCS3**.

---

## Setup

**Step 1:** In RPCS3, go to:  
`GCI → Properties → Network → DNS → 127.0.0.1`

**Step 2:** Generate .ELF File:
`RPCS3 → Utilities → Decrypt PS3 Binaries → Eboot.bin`

**Step 3:** Drag .ELF file to Hex Editor and replace:
`"https://%s.api.gamespy"` with `"http://%s.api.gamespy`

**Step 4:** Boot modified .ELF in RPCS3 to open GCI.

That’s it for now. If it breaks, i'ts expected.
