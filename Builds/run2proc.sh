#!/bin/bash
cd /c/Projects/DS/UniNet/Builds
UNITY="/c/Program Files/Unity/Hub/Editor/6000.0.83f1/Editor/Unity.exe"
rm -f 2proc-server.log 2proc-client.log
"$UNITY" -batchmode -nographics -projectPath "C:\Projects\DS\UniNet\Builds\2ProcServer" -executeMethod UniNet.Tests.TwoProcessRunner.Run --uninet-role=server -logFile "C:\Projects\DS\UniNet\Builds\2proc-server.log" &
SPID=$!
sleep 10
"$UNITY" -batchmode -nographics -projectPath "C:\Projects\DS\UniNet\Builds\2ProcClient" -executeMethod UniNet.Tests.TwoProcessRunner.Run --uninet-role=client -logFile "C:\Projects\DS\UniNet\Builds\2proc-client.log" &
CPID=$!
wait $CPID; echo "CLIENT_EXIT=$?"
wait $SPID; echo "SERVER_EXIT=$?"
