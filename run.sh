#!/bin/bash

# Conduit Run Script for Linux/Mac

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Default values
COMMAND=${1:-run}
RELEASE=false
WATCH=false

# Parse additional arguments
for arg in "$@"; do
    case $arg in
        --release)
            RELEASE=true
            ;;
        --watch)
            WATCH=true
            ;;
    esac
done

write_header() {
    echo -e "\n${CYAN}$1${NC}"
    echo -e "${CYAN}$(printf '%*s' ${#1} | tr ' ' '-')${NC}"
}

build_solution() {
    write_header "Building Conduit"
    
    if [ "$RELEASE" = true ]; then
        dotnet build -c Release
    else
        dotnet build -c Debug
    fi
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}Build failed!${NC}"
        exit 1
    fi
    
    echo -e "${GREEN}Build completed successfully!${NC}"
}

run_application() {
    write_header "Starting Conduit"
    
    cd src/Conduit.Web
    
    if [ "$WATCH" = true ]; then
        echo -e "${YELLOW}Starting in watch mode (hot reload enabled)...${NC}"
        dotnet watch run
    else
        dotnet run
    fi
    
    cd ../..
}

run_docker() {
    write_header "Starting Conduit with Docker"
    
    docker-compose up --build
}

run_docker_dev() {
    write_header "Starting Development Database"
    
    docker-compose -f docker-compose.dev.yml up -d
    
    echo -e "\n${GREEN}Development database started!${NC}"
    echo -e "${YELLOW}Connection string: Server=localhost,1433;Database=Conduit;User Id=sa;Password=Dev!Password123;TrustServerCertificate=True${NC}"
    echo -e "${YELLOW}Adminer URL: http://localhost:8080${NC}"
}

run_installer() {
    write_header "Running Conduit Installer"
    
    cd src/Conduit.Installer
    dotnet run
    cd ../..
}

run_tests() {
    write_header "Running Tests"
    
    dotnet test
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}Tests failed!${NC}"
        exit 1
    fi
    
    echo -e "${GREEN}All tests passed!${NC}"
}

clean_solution() {
    write_header "Cleaning Solution"
    
    # Clean bin and obj directories
    find . -type d -name bin -o -name obj | xargs rm -rf
    
    # Clean Docker volumes (with confirmation)
    read -p "Clean Docker volumes? (y/N) " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        docker-compose down -v
        docker-compose -f docker-compose.dev.yml down -v
    fi
    
    echo -e "${GREEN}Clean completed!${NC}"
}

# Show usage
show_usage() {
    echo "Usage: ./run.sh [command] [options]"
    echo ""
    echo "Commands:"
    echo "  build       Build the solution"
    echo "  run         Build and run the application (default)"
    echo "  docker      Run with Docker Compose"
    echo "  docker-dev  Run development database only"
    echo "  install     Run the installer"
    echo "  test        Run tests"
    echo "  clean       Clean build artifacts and Docker volumes"
    echo ""
    echo "Options:"
    echo "  --release   Build in Release mode"
    echo "  --watch     Run with hot reload (watch mode)"
}

# Main script execution
case $COMMAND in
    build)
        build_solution
        ;;
    run)
        build_solution
        run_application
        ;;
    docker)
        run_docker
        ;;
    docker-dev)
        run_docker_dev
        ;;
    install)
        run_installer
        ;;
    test)
        run_tests
        ;;
    clean)
        clean_solution
        ;;
    help|--help|-h)
        show_usage
        ;;
    *)
        echo -e "${RED}Unknown command: $COMMAND${NC}"
        show_usage
        exit 1
        ;;
esac

echo -e "\n${GREEN}Done!${NC}"