cask "postsharp" do
  version "1.11.12"
  sha256 "a903fa917d0ada99ec53ff322549a9ffb74569d8f2c55853aa6b040f0cfc02c1"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end