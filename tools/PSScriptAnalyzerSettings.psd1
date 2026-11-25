# PSScriptAnalyzer settings for tools\*.ps1, run by tools\Test-Layer8Regression.ps1's Security section. Invoke-ScriptAnalyzer
# also picks this file up by itself for a script in this folder.
#
# Security rules only, the same choice as the C# build (Directory.Build.props). InjectionHunter's rules run alongside
# these, all of them, in a second pass (-CustomRulePath), so they aren't listed here.
#
# The default rules left out, with what they found in tools\*.ps1 on November 25, 2026 (439 findings, none security):
#   PSAvoidUsingPositionalParameters (326)             Style.
#   PSAvoidUsingWriteHost (66)                         The scripts talk to the person running them; Write-Host is meant.
#   PSUseSingularNouns (16)                            Function names.
#   PSUseShouldProcessForStateChangingFunctions (16)   No -WhatIf on lab-data and helper functions; the scripts say what
#                                                      they change.
#   PSAvoidUsingEmptyCatchBlock (6)                    Each read: a failed ping, port or parse falls through to "no", and
#                                                      a path GetFullPath refuses is skipped, not deleted.
#   PSAvoidUsingWMICmdlet (5)                          Get-WmiObject against CIM: not a security question.
#   PSReviewUnusedParameter (4)                        Unused parameters.
@{
    IncludeRules = @(
        'PSAvoidUsingInvokeExpression'
        'PSAvoidUsingPlainTextForPassword'
        'PSAvoidUsingConvertToSecureStringWithPlainText'
        'PSAvoidUsingUsernameAndPasswordParams'
        'PSUsePSCredentialType'
        'PSAvoidUsingAllowUnencryptedAuthentication'
        'PSAvoidUsingBrokenHashAlgorithms'
        'PSAvoidUsingComputerNameHardcoded'
    )
}
