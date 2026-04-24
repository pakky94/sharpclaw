package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/spf13/cobra"

	"sharpclaw-tui/internal/compose"
	"sharpclaw-tui/internal/config"
	"sharpclaw-tui/internal/tui"
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed loading config: %v\n", err)
		os.Exit(1)
	}

	workdir, err := os.Getwd()
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed resolving cwd: %v\n", err)
		os.Exit(1)
	}

	var (
		apiURL         = cfg.APIBaseURL
		composeCmd     = cfg.ComposeCommand
		composeFile    = cfg.ComposeFile
		composeWorkdir = workdir
	)

	rootCmd := &cobra.Command{
		Use:   "sharpclaw-tui",
		Short: "SharpClaw terminal UI and compose manager",
		RunE: func(cmd *cobra.Command, args []string) error {
			runner := compose.Runner{
				ComposeCommand: composeCmd,
				ComposeFile:    composeFile,
				WorkingDir:     composeWorkdir,
			}
			activeCfg := config.Config{
				APIBaseURL:     apiURL,
				ComposeCommand: composeCmd,
				ComposeFile:    composeFile,
			}
			return tui.Run(activeCfg, runner)
		},
	}

	rootCmd.PersistentFlags().StringVar(&apiURL, "api-url", apiURL, "SharpClaw API base URL")
	rootCmd.PersistentFlags().StringVar(&composeCmd, "compose-command", composeCmd, "Compose binary (docker or podman)")
	rootCmd.PersistentFlags().StringVar(&composeFile, "compose-file", composeFile, "Compose file path")
	rootCmd.PersistentFlags().StringVar(&composeWorkdir, "workdir", composeWorkdir, "Working directory for compose commands")

	composeRoot := &cobra.Command{Use: "compose", Short: "Manage SharpClaw docker-compose containers"}
	composeStart := &cobra.Command{
		Use:   "start [service...]",
		Short: "Start compose services",
		RunE: func(cmd *cobra.Command, args []string) error {
			build, _ := cmd.Flags().GetBool("build")
			runner := compose.Runner{ComposeCommand: composeCmd, ComposeFile: composeFile, WorkingDir: composeWorkdir}
			out, err := runner.Start(args, build)
			if err != nil {
				return err
			}
			fmt.Println(out)
			return nil
		},
	}
	composeStart.Flags().Bool("build", false, "Run compose up with --build")

	composeStop := &cobra.Command{
		Use:   "stop [service...]",
		Short: "Stop compose services (or all with down)",
		RunE: func(cmd *cobra.Command, args []string) error {
			volumes, _ := cmd.Flags().GetBool("volumes")
			runner := compose.Runner{ComposeCommand: composeCmd, ComposeFile: composeFile, WorkingDir: composeWorkdir}
			out, err := runner.Stop(args, volumes)
			if err != nil {
				return err
			}
			fmt.Println(out)
			return nil
		},
	}
	composeStop.Flags().Bool("volumes", false, "When stopping all, remove volumes (down -v)")

	composeStatus := &cobra.Command{
		Use:   "status",
		Short: "Show compose service status",
		RunE: func(cmd *cobra.Command, args []string) error {
			runner := compose.Runner{ComposeCommand: composeCmd, ComposeFile: composeFile, WorkingDir: composeWorkdir}
			out, err := runner.Status()
			if err != nil {
				return err
			}
			fmt.Println(out)
			return nil
		},
	}

	composeRoot.AddCommand(composeStart, composeStop, composeStatus)
	rootCmd.AddCommand(composeRoot)

	configCmd := &cobra.Command{Use: "config", Short: "Manage ~/.sharpclaw/config.json"}
	configShow := &cobra.Command{
		Use:   "show",
		Short: "Print active config",
		RunE: func(cmd *cobra.Command, args []string) error {
			path, err := config.FilePath()
			if err != nil {
				return err
			}
			fmt.Printf("config: %s\n", path)
			fmt.Printf("api_url: %s\n", apiURL)
			fmt.Printf("compose_command: %s\n", composeCmd)
			fmt.Printf("compose_file: %s\n", composeFile)
			fmt.Printf("workdir: %s\n", composeWorkdir)
			return nil
		},
	}

	configSet := &cobra.Command{
		Use:   "set",
		Short: "Persist selected config values",
		RunE: func(cmd *cobra.Command, args []string) error {
			next := config.Config{
				APIBaseURL:     apiURL,
				ComposeCommand: composeCmd,
				ComposeFile:    composeFile,
			}
			if err := config.Save(next); err != nil {
				return err
			}
			path, err := config.FilePath()
			if err != nil {
				return err
			}
			fmt.Printf("saved config to %s\n", path)
			return nil
		},
	}

	configInit := &cobra.Command{
		Use:   "init",
		Short: "Create ~/.sharpclaw/config.json with current flags",
		RunE: func(cmd *cobra.Command, args []string) error {
			next := config.Config{APIBaseURL: apiURL, ComposeCommand: composeCmd, ComposeFile: composeFile}
			if err := config.Save(next); err != nil {
				return err
			}
			path, _ := config.FilePath()
			fmt.Printf("initialized config at %s\n", path)
			return nil
		},
	}

	configCmd.AddCommand(configShow, configSet, configInit)
	rootCmd.AddCommand(configCmd)

	rootCmd.SetHelpFunc(func(cmd *cobra.Command, args []string) {
		_ = cmd.Root().Usage()
		fmt.Println("\nExamples:")
		fmt.Println("  sharpclaw-tui")
		fmt.Println("  sharpclaw-tui compose start --build")
		fmt.Println("  sharpclaw-tui compose stop --volumes")
		fmt.Println("  sharpclaw-tui --api-url http://localhost:5223")
		fmt.Println("  sharpclaw-tui config set --api-url http://localhost:5846")
	})

	rootCmd.PersistentPreRunE = func(cmd *cobra.Command, args []string) error {
		composeFile = strings.TrimSpace(composeFile)
		if composeFile == "" {
			return fmt.Errorf("compose-file cannot be empty")
		}
		if !filepath.IsAbs(composeFile) {
			composeFile = filepath.Clean(composeFile)
		}
		return nil
	}

	if err := rootCmd.Execute(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
