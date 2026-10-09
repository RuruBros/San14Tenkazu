// Headless: applies names.txt (rva name [signature]) to the program, then writes the decompiled C of the
// functions at the given RVAs. Run by decompile.py; arguments: <names.txt> <out.c> <rva>...
// @category San14
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;

public class Decompile extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Path names = Paths.get(args[0]);
        Path out = Paths.get(args[1]);
        Address base = currentProgram.getImageBase();
        applyNames(names, base);

        DecompInterface ifc = new DecompInterface();
        DecompileOptions opts = new DecompileOptions();
        ifc.setOptions(opts);
        ifc.toggleCCode(true);
        ifc.toggleSyntaxTree(false);
        ifc.openProgram(currentProgram);
        StringBuilder sb = new StringBuilder();
        for (int k = 2; k < args.length; k++) {
            // "@rva": the function holding the address (a chained chunk belongs to its parent). "rva": the function
            // starting there; where Ghidra merged it into the code before it (a call target it did not split off),
            // the merged function is cut at the address and both are made again (in this read-only run only).
            boolean containing = args[k].startsWith("@");
            long rva = Long.decode(containing ? args[k].substring(1) : args[k]);
            Address a = base.add(rva);
            Function f = getFunctionAt(a);
            if (f == null) {
                Function outer = getFunctionContaining(a);
                if (containing && outer != null) f = outer;
                else {
                    Address outerEntry = outer == null ? null : outer.getEntryPoint();
                    if (outer != null) removeFunction(outer);
                    if (getInstructionAt(a) == null) new DisassembleCommand(a, null, true).applyTo(currentProgram, monitor);
                    ghidra.app.cmd.function.CreateFunctionCmd make = new ghidra.app.cmd.function.CreateFunctionCmd(a);
                    if (!make.applyTo(currentProgram, monitor))
                        println("create " + args[k] + ": " + make.getStatusMsg() + " outer=" + outer + " insn=" + getInstructionAt(a)
                                + " data=" + getDataAt(a) + " still in=" + getFunctionContaining(a));
                    f = getFunctionAt(a);
                    if (outerEntry != null && !outerEntry.equals(a)) new ghidra.app.cmd.function.CreateFunctionCmd(outerEntry).applyTo(currentProgram, monitor);
                }
            }
            if (f == null) { sb.append("// ").append(args[k]).append(": no function\n\n"); continue; }
            DecompileResults r = ifc.decompileFunction(f, 180, monitor);
            long frva = f.getEntryPoint().subtract(base);
            sb.append(String.format("// ===== rva 0x%x %s%n", frva, f.getName()));
            if (r == null || !r.decompileCompleted()) {
                sb.append("// decompile failed: ").append(r == null ? "null" : r.getErrorMessage()).append("\n\n");
                continue;
            }
            sb.append(r.getDecompiledFunction().getC()).append("\n");
        }
        Files.write(out, sb.toString().getBytes(StandardCharsets.UTF_8));
        ifc.dispose();
    }

    // names.txt: "<rva> <name> [c prototype]" per line, # comments. Functions get the name (and the prototype when
    // given); other addresses get a label.
    private void applyNames(Path file, Address base) throws Exception {
        if (!Files.exists(file)) return;
        SymbolTable st = currentProgram.getSymbolTable();
        for (String line : Files.readAllLines(file, StandardCharsets.UTF_8)) {
            line = line.trim();
            if (line.isEmpty() || line.startsWith("#")) continue;
            String[] p = line.split("\\s+", 3);
            Address a = base.add(Long.decode(p[0]));
            String name = p[1];
            Function f = getFunctionAt(a);
            if (f != null) {
                if (!f.getName().equals(name)) f.setName(name, SourceType.USER_DEFINED);
                if (p.length > 2 && p[2].contains("(")) {
                    try {
                        ghidra.app.util.parser.FunctionSignatureParser parser =
                            new ghidra.app.util.parser.FunctionSignatureParser(currentProgram.getDataTypeManager(), null);
                        ghidra.program.model.data.FunctionDefinitionDataType sig = parser.parse(f.getSignature(), p[2]);
                        new ghidra.app.cmd.function.ApplyFunctionSignatureCmd(a, sig, SourceType.USER_DEFINED).applyTo(currentProgram, monitor);
                    } catch (Exception e) {
                        println("signature " + p[0] + ": " + e.getMessage());
                    }
                }
            } else {
                Symbol s = st.getPrimarySymbol(a);
                if (s == null || !s.getName().equals(name)) st.createLabel(a, name, SourceType.USER_DEFINED);
            }
        }
    }
}
