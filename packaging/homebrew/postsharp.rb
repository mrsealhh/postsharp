cask "postsharp" do
  version "1.11.22"
  sha256 "ea8659fabcd3369248b86427f49481b06913813e6ad506b3fd7622fdee20145f"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end