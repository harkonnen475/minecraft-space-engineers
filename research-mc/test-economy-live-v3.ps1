param([int]$TimeoutSeconds=90)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
$mcControl=Join-Path $PSScriptRoot 'economy-control-v3.ps1'
$seControl=Join-Path $bridgeRoot 'semc-control.ps1'
$expectedWorld='6b361790-8790-41bf-abbb-a2f4e30e9016'
$expectedMc=[IO.Path]::GetFullPath((Join-Path $bridgeRoot 'minecraft-backend\saves\SEMC Backend')).TrimEnd('\')
$expectedSe=[IO.Path]::GetFullPath((Join-Path $env:APPDATA 'SpaceEngineers\Saves\76561198055314775\SEMC Lab - Bridge Test')).TrimEnd('\')
$runToken=[guid]::NewGuid().ToString('N')
$results=[Collections.Generic.List[object]]::new()
$script:pendingMcTransaction=$null
$script:currentTest='World and transaction preconditions'

function Read-Json([string]$Name){
    for($attempt=0;$attempt -lt 20;$attempt++){
        try{return Get-Content -LiteralPath (Join-Path $bridgeRoot $Name) -Raw | ConvertFrom-Json}catch{Start-Sleep -Milliseconds 50}
    }
    throw "Could not read $Name"
}
function Assert-That([bool]$Condition,[string]$Reason){if(!$Condition){throw $Reason}}
function Mc([string]$Operation,[hashtable]$Arguments=@{},[string]$RequestId=([guid]::NewGuid().ToString('N'))){
    $response=& $mcControl -Operation $Operation -Arguments $Arguments -RequestId $RequestId -TimeoutSeconds $TimeoutSeconds
    Assert-That ($response.worldId -eq $expectedWorld) 'Response belongs to another world'
    Assert-That ([bool]$response.ok) ("Minecraft operation rejected: "+$response.error)
    return $response
}
function Se([hashtable]$Arguments=@{operation='inspect'}){
    $state=& $seControl -Action economy -Extra $Arguments | ConvertFrom-Json
    Assert-That ([IO.Path]::GetFullPath([string]$state.world).TrimEnd('\') -eq $expectedSe) 'Space Engineers world path mismatch'
    return $state.economy
}
function Bag {return Read-Json 'economy-state-v3.json'}
function Mc-Count([string]$ItemId){$sum=0;foreach($slot in (Bag).slots){if($slot.itemId -eq $ItemId){$sum+=[int]$slot.count}};return $sum}
function Se-Count([string]$ItemId){$sum=[decimal]0;foreach($slot in (Se).items){if($slot.id -eq $ItemId){$sum+=[decimal]$slot.amount}};return $sum}
function Se-Signature {return (((Se).items | Where-Object engine -EQ 'SE' | Sort-Object id,amount | ForEach-Object {"$($_.id)=$($_.amount)"}) -join '|')}
function Wait-HostIdle {
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do{
        $state=Se
        if(!$state.busy){Assert-That (!$state.blocked) 'Host transaction is blocked';Start-Sleep -Milliseconds 650;return $state}
        Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $deadline)
    throw 'Host transaction did not settle before timeout'
}
function Host-Craft([string]$RecipeId,[int]$Count=1){
    $null=Wait-HostIdle
    $null=Se @{operation='craft';recipeId=$RecipeId;count=$Count}
    return Wait-HostIdle
}
function Grant([string]$ItemId,[int]$Count){$null=Mc 'grant' @{itemId=$ItemId;count=$Count}}
function Grant-Stack([hashtable]$Stack){$null=Mc 'grant' @{stackData=$Stack}}
function Record([string]$Name,[hashtable]$Measurements=@{}){$results.Add(@{name=$Name;status='passed';measurements=$Measurements});Write-Output "PASS $Name"}
function Named-Slot([string]$Name){return @((Bag).slots | Where-Object {$_.count -gt 0 -and $_.displayName -eq $Name}) | Select-Object -First 1}
function Begin-Tx([hashtable]$Arguments){$script:pendingMcTransaction=$Arguments.txId;return Mc 'prepare' $Arguments}
function Finish-Tx([string]$Operation){$response=Mc $Operation @{txId=$script:pendingMcTransaction};if($response.status -in @('committed','aborted','unknown')){$script:pendingMcTransaction=$null};return $response}
function Save-Results([bool]$Passed){
    $report=@{version=3;timestampUtc=[DateTime]::UtcNow.ToString('o');passed=$Passed;tests=@($results.ToArray());scope='Local cloned worlds; diagnostic supplies retained; no inventory resets'}
    $target=Join-Path $bridgeRoot 'qa-live-v3-tests.json';$temporary=$target+'.test.tmp'
    [IO.File]::WriteAllText($temporary,($report|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $target){[IO.File]::Replace($temporary,$target,[NullString]::Value)}else{[IO.File]::Move($temporary,$target)}
}

try {
    $world=Read-Json 'world-v2.json';$state=Bag
    Assert-That ($world.worldId -eq $expectedWorld -and $state.worldId -eq $expectedWorld) 'Unexpected world UUID'
    Assert-That ([IO.Path]::GetFullPath([string]$world.worldPath).TrimEnd('\') -eq $expectedMc) 'Minecraft clone path mismatch'
    Assert-That ($world.dimension -eq 'minecraft:overworld') 'Unexpected Minecraft dimension'
    Assert-That (([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-[long]$state.timestamp) -lt 4000) 'Native inventory is stale'
    Assert-That (!$state.pendingTransaction) 'An existing native transaction must be recovered first'
    $null=Wait-HostIdle
    $null=Mc 'snapshot'
    Record $script:currentTest @{clonedWorldGuard=$true;noPendingTransaction=$true}

    $script:currentTest='Mixed Minecraft and Space Engineers iron produces one bucket'
    $beforeMcIron=Mc-Count 'minecraft:iron_ingot';$beforeSeIron=Se-Count 'se:MyObjectBuilder_Ingot/Iron';$beforeBuckets=Mc-Count 'minecraft:bucket'
    # The exact 2+1 test requires no existing MC iron, so it never silently consumes prior supplies.
    Assert-That ($beforeMcIron -eq 0) 'Exact mixed 2+1 test requires zero pre-existing MC iron; no inventory reset was performed'
    Grant 'minecraft:iron_ingot' 2
    $null=Se @{operation='grant-se';itemId='se:MyObjectBuilder_Ingot/Iron';amount=1}
    Start-Sleep -Milliseconds 700
    $null=Host-Craft 'mc:minecraft:bucket'
    $afterMcIron=Mc-Count 'minecraft:iron_ingot';$afterSeIron=Se-Count 'se:MyObjectBuilder_Ingot/Iron';$afterBuckets=Mc-Count 'minecraft:bucket'
    Assert-That ($afterMcIron -eq $beforeMcIron -and $afterSeIron -eq $beforeSeIron -and $afterBuckets -eq $beforeBuckets+1) 'Mixed bucket did not conserve exactly two native MC ingots and one SE kilogram'
    Record $script:currentTest @{minecraftInputs=2;spaceEngineersInputs=1;outputs=1}

    $script:currentTest='Three automatically discovered cherry logs produce twelve planks'
    $beforeLogs=Mc-Count 'minecraft:cherry_log';$beforePlanks=Mc-Count 'minecraft:cherry_planks'
    Grant 'minecraft:cherry_log' 3;Start-Sleep -Milliseconds 700
    $null=Host-Craft 'mc:minecraft:cherry_planks' 3
    Assert-That ((Mc-Count 'minecraft:cherry_log') -eq $beforeLogs -and (Mc-Count 'minecraft:cherry_planks') -eq $beforePlanks+12) 'Native cherry recipe quantities disagree'
    Record $script:currentTest @{inputs=3;outputs=12}

    $script:currentTest='Fragmented component-bearing stacks craft one batch'
    $fragmentA='SEMC QA fragment A '+$runToken;$fragmentB='SEMC QA fragment B '+$runToken
    Grant-Stack @{id='minecraft:iron_block';count=1;components=@{'minecraft:custom_name'=$fragmentA}}
    Grant-Stack @{id='minecraft:iron_block';count=1;components=@{'minecraft:custom_name'=$fragmentB}}
    $a=Named-Slot $fragmentA;$b=Named-Slot $fragmentB;Assert-That ($null -ne $a -and $null -ne $b) 'Fragmented test stacks were not present'
    $beforeIron=Mc-Count 'minecraft:iron_ingot'
    $tx='live-fragments-'+$runToken
    $transactionArgs=@{txId=$tx;takes=@(@{slot=[int]$a.slot;count=1;stackHash=$a.stackHash},@{slot=[int]$b.slot;count=1;stackHash=$b.stackHash});craft=@{recipeId='minecraft:iron_ingot_from_iron_block';width=1;height=1;count=2;grids=@(@(@{slot=[int]$a.slot;itemId='minecraft:iron_block'}),@(@{slot=[int]$b.slot;itemId='minecraft:iron_block'}))}}
    $prepared=Begin-Tx $transactionArgs;Assert-That ($prepared.status -eq 'prepared') 'Fragmented recipe did not reserve inputs'
    $committed=Finish-Tx 'commit';Assert-That ($committed.status -eq 'committed') 'Fragmented batch did not commit'
    Assert-That ((Mc-Count 'minecraft:iron_ingot') -eq $beforeIron+18 -and $null -eq (Named-Slot $fragmentA) -and $null -eq (Named-Slot $fragmentB)) 'Fragmented batch output or ingredient conservation failed'
    Record $script:currentTest @{separateSourceStacks=2;outputs=18;componentsPreservedUntilConsumption=$true}

    $script:currentTest='Escrow abort preserves enchanted tools and nested container contents'
    $axeName='SEMC QA axe '+$runToken;$boxName='SEMC QA box '+$runToken
    $axe=@{id='minecraft:diamond_axe';count=1;components=@{'minecraft:custom_name'=$axeName;'minecraft:damage'=17;'minecraft:enchantments'=@{'minecraft:unbreaking'=3}}}
    $box=@{id='minecraft:shulker_box';count=1;components=@{'minecraft:custom_name'=$boxName;'minecraft:container'=@(@{slot=0;item=$axe},@{slot=1;item=@{id='minecraft:iron_ingot';count=7;components=@{'minecraft:custom_name'='Nested named material'}}})}}
    Grant-Stack $axe;Grant-Stack $box
    $tool=Named-Slot $axeName;$container=Named-Slot $boxName
    Assert-That ($null -ne $tool -and $null -ne $container) 'Component fixtures missing'
    $toolHash=$tool.stackHash;$boxHash=$container.stackHash
    $null=Begin-Tx @{txId=('live-components-'+$runToken);takes=@(@{slot=[int]$tool.slot;count=1;stackHash=$toolHash},@{slot=[int]$container.slot;count=1;stackHash=$boxHash});gives=@()}
    Assert-That ($null -eq (Named-Slot $axeName) -and $null -eq (Named-Slot $boxName)) 'Escrow did not reserve component-bearing stacks'
    $aborted=Finish-Tx 'abort';Assert-That ($aborted.status -eq 'aborted') 'Component escrow did not abort'
    Assert-That ((Named-Slot $axeName).stackHash -eq $toolHash -and (Named-Slot $boxName).stackHash -eq $boxHash) 'Abort changed native stack components'
    Record $script:currentTest @{tools=1;nestedContainers=1;exactStackHashesRestored=$true}

    $script:currentTest='Repeated creative grant request does not duplicate items'
    $beforeCopper=Mc-Count 'minecraft:copper_ingot';$grantId='live-grant-'+$runToken
    $null=Mc 'grant' @{itemId='minecraft:copper_ingot';count=2} $grantId
    $null=Mc 'snapshot'
    $null=Mc 'status' @{txId=('creative-grant:'+$grantId)}
    $null=Mc 'grant' @{itemId='minecraft:copper_ingot';count=2} $grantId
    Assert-That ((Mc-Count 'minecraft:copper_ingot') -eq $beforeCopper+2) 'Repeated grant duplicated native stacks'
    Record $script:currentTest @{requests=2;grantedItems=2}

    $script:currentTest='Repeated prepare and commit receipts do not consume or produce twice'
    Grant 'minecraft:amethyst_shard' 3
    $source=@((Bag).slots | Where-Object {$_.itemId -eq 'minecraft:amethyst_shard' -and $_.count -ge 1}) | Select-Object -First 1
    $beforeAmethyst=Mc-Count 'minecraft:amethyst_shard';$beforeQuartz=Mc-Count 'minecraft:quartz'
    $tx='live-idempotency-'+$runToken;$transactionArgs=@{txId=$tx;takes=@(@{slot=[int]$source.slot;count=1;stackHash=$source.stackHash});gives=@(@{id='minecraft:quartz';count=1})}
    $null=Begin-Tx $transactionArgs;$again=Mc 'prepare' $transactionArgs
    Assert-That ($again.status -eq 'prepared' -and (Mc-Count 'minecraft:amethyst_shard') -eq $beforeAmethyst-1) 'Prepare retry consumed additional items'
    $null=Finish-Tx 'commit';$again=Mc 'commit' @{txId=$tx};$receipt=Mc 'status' @{txId=$tx}
    Assert-That ($again.status -eq 'committed' -and $receipt.status -eq 'committed' -and (Mc-Count 'minecraft:amethyst_shard') -eq $beforeAmethyst-1 -and (Mc-Count 'minecraft:quartz') -eq $beforeQuartz+1) 'Commit retry duplicated output or ingredient consumption'
    Record $script:currentTest @{prepareCalls=2;commitCalls=2;consumed=1;produced=1}

    $script:currentTest='Insufficient-material crafting leaves both inventories unchanged'
    $null=Wait-HostIdle;$beforeBag=(Bag).inventoryHash;$beforeSe=Se-Signature
    Assert-That ((Mc-Count 'minecraft:nether_star') -lt 64) 'Insufficient-material fixture unexpectedly has enough nether stars'
    $missing=Host-Craft 'mc:minecraft:beacon' 64
    Assert-That ($missing.status -match 'Brakuje') 'Crafting was not explicitly rejected for insufficient materials'
    Assert-That ((Bag).inventoryHash -eq $beforeBag -and (Se-Signature) -eq $beforeSe) 'Rejected crafting modified an inventory'
    Record $script:currentTest @{inventoryChanges=0}
    Save-Results $true
} catch {
    $originalFailure=$_
    $cleanup='No standalone native transaction was pending'
    if($script:pendingMcTransaction){
        try{
            $receipt=Mc 'status' @{txId=$script:pendingMcTransaction}
            if($receipt.status -eq 'prepared'){$null=Finish-Tx 'abort';$cleanup='Standalone native escrow was returned'}
            elseif($receipt.status -in @('committed','aborted','unknown')){$script:pendingMcTransaction=$null;$cleanup='Native transaction already settled'}
            else{$cleanup='Native transaction retained for explicit recovery'}
        }catch{$cleanup='Native transaction retained for explicit recovery'}
    }
    $results.Add(@{name=$script:currentTest;status='failed';reason='Assertion or engine operation failed; detailed diagnostics remain in the console';cleanup=$cleanup})
    Save-Results $false
    throw $originalFailure
}
