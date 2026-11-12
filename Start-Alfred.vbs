Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
root = fso.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root
shell.Environment("PROCESS")("TEMP") = root & "\temp"
shell.Environment("PROCESS")("TMP") = root & "\temp"
shell.Run Chr(34) & root & "\Alfred.exe" & Chr(34), 1, False
