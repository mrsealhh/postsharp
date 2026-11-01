cask "postsharp" do
  version "1.11.6"
  sha256 "a8ab4da8fa1484c4c031348efff66c16ca4eec49ea02d338dda68258c7193d48"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end