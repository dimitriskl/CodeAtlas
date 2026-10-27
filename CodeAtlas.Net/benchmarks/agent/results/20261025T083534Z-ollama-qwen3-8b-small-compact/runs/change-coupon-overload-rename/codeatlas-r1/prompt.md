Repository: shop. The current directory is the repository root; the solution is Shop.slnx.

Task: Rename the overload DiscountCalculator.Apply(Order, Coupon) to ApplyCoupon and update its callers. Keep Apply(Order, decimal) and its callers unchanged. The solution must build.

Edit the files in place, then check with: dotnet build Shop.slnx

---
CodeAtlas, a semantic index of this solution, is available through Bash. Run it exactly in this form:
C:/Users/photd/AppData/Local/Programs/Python/Python315/python.exe C:/temp/codeatlas-bench/suites/small/tools/codeatlas.py find-symbol --index "C:/temp/codeatlas-bench/suites/small/work/codeatlas/.codeatlas/solutions/Shop.slnx-6D45B1CB9D7AC82F62BDA9C627A925A92C6D0A7B8A681656DBB6078387043B01/current.json" --name <Name> [--kind method|type|all]
C:/Users/photd/AppData/Local/Programs/Python/Python315/python.exe C:/temp/codeatlas-bench/suites/small/tools/codeatlas.py find-references --index "C:/temp/codeatlas-bench/suites/small/work/codeatlas/.codeatlas/solutions/Shop.slnx-6D45B1CB9D7AC82F62BDA9C627A925A92C6D0A7B8A681656DBB6078387043B01/current.json" --symbol <id from find-symbol>
C:/Users/photd/AppData/Local/Programs/Python/Python315/python.exe C:/temp/codeatlas-bench/suites/small/tools/codeatlas.py find-callers --index "C:/temp/codeatlas-bench/suites/small/work/codeatlas/.codeatlas/solutions/Shop.slnx-6D45B1CB9D7AC82F62BDA9C627A925A92C6D0A7B8A681656DBB6078387043B01/current.json" --symbol <id>
Output is JSON with file paths and lines. Use it to find symbols, overloads, implementations and callers, then read those files. After edits, queries return stale_index; continue with Grep and Read. Solution: "C:/temp/codeatlas-bench/suites/small/work/codeatlas/Shop.slnx"; overview: "C:/temp/codeatlas-bench/suites/small/work/codeatlas/.codeatlas/solutions/Shop.slnx-6D45B1CB9D7AC82F62BDA9C627A925A92C6D0A7B8A681656DBB6078387043B01/generations/02642382a02449ac95f40ebacb1c409a/overview.md".