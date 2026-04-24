package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

type Config struct {
	APIBaseURL     string `json:"api_base_url"`
	ComposeCommand string `json:"compose_command"`
	ComposeFile    string `json:"compose_file"`
}

func defaultConfig() Config {
	return Config{
		APIBaseURL:     "http://localhost:5846",
		ComposeCommand: "docker",
		ComposeFile:    "docker-compose.yml",
	}
}

func DirPath() (string, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("resolve home directory: %w", err)
	}

	return filepath.Join(home, ".sharpclaw"), nil
}

func FilePath() (string, error) {
	dir, err := DirPath()
	if err != nil {
		return "", err
	}

	return filepath.Join(dir, "config.json"), nil
}

func Load() (Config, error) {
	cfg := defaultConfig()

	path, err := FilePath()
	if err != nil {
		return cfg, err
	}

	content, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			if saveErr := Save(cfg); saveErr != nil {
				return cfg, saveErr
			}
			return cfg, nil
		}
		return cfg, fmt.Errorf("read config: %w", err)
	}

	if err := json.Unmarshal(content, &cfg); err != nil {
		return cfg, fmt.Errorf("parse config: %w", err)
	}

	if cfg.APIBaseURL == "" {
		cfg.APIBaseURL = "http://localhost:5846"
	}
	if cfg.ComposeCommand == "" {
		cfg.ComposeCommand = "docker"
	}
	if cfg.ComposeFile == "" {
		cfg.ComposeFile = "docker-compose.yml"
	}

	return cfg, nil
}

func Save(cfg Config) error {
	dir, err := DirPath()
	if err != nil {
		return err
	}

	if err := os.MkdirAll(dir, 0o755); err != nil {
		return fmt.Errorf("create config directory: %w", err)
	}

	path := filepath.Join(dir, "config.json")
	blob, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return fmt.Errorf("encode config: %w", err)
	}

	if err := os.WriteFile(path, blob, 0o644); err != nil {
		return fmt.Errorf("write config: %w", err)
	}

	return nil
}
